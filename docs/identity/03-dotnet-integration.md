# 3. .NET Integration — Clean Architecture Layering

How token validation, claims mapping and tenant resolution are distributed across
API / Application / Infrastructure, and what each Cube RM service consumes.

---

## 3.1 The layering rule

> **Authentication is an API-layer concern. Tenant context is an Application-layer
> abstraction. Everything that touches Entra or PostgreSQL is Infrastructure. CQRS
> handlers know about `ICubeTenantContext` and nothing else.**

A handler must never see a `ClaimsPrincipal`, a JWT, or the word "Entra". If a handler
needs to know who the caller is, it takes `ICubeTenantContext`. This keeps handlers
unit-testable with a two-line fake and means a future identity change touches one project.

| Layer | Owns | Must not contain |
| --- | --- | --- |
| **Domain** | Organisation, domain-routing rules, org status invariants | Any framework type |
| **Application** | `ICubeTenantContext` (abstraction), `TenantIsolationBehaviour`, authorization policy evaluation | `ClaimsPrincipal`, `HttpContext`, Graph, EF |
| **Infrastructure** | EF Core, RLS interceptor, Graph client, issuer registry cache | ASP.NET pipeline wiring |
| **API** | JwtBearer configuration, claims → tenant context mapping, endpoints | Business rules |
| **SharedKernel.Auth** | The above API + Application plumbing, packaged for reuse by *every* Cube RM service | Anything product-specific |

## 3.2 Project structure

```
src/
├── CubeRM.SharedKernel.Auth/            ← NuGet package, consumed by EVERY Cube RM API
│   ├── Abstractions/
│   │   ├── ICubeTenantContext.cs        ← the one thing handlers depend on
│   │   ├── ITenantScopedRequest.cs      ← marker for CQRS requests
│   │   └── ITrustedIssuerRegistry.cs
│   ├── Claims/
│   │   ├── CubeClaimTypes.cs            ← claim name constants, versioned
│   │   └── ClaimsPrincipalExtensions.cs
│   ├── Configuration/
│   │   └── CubeAuthenticationOptions.cs
│   ├── Issuers/
│   │   ├── MultiIssuerConfigurationManager.cs
│   │   └── TrustedIssuerRegistry.cs     ← cached (issuer → org) resolution
│   └── Extensions/
│       ├── AddCubeAuthenticationExtensions.cs
│       └── AddCubeTenantIsolationExtensions.cs
│
├── CubeRM.Identity.Domain/              ← identity service: pure model
├── CubeRM.Identity.Application/         ← identity service: CQRS + behaviours
├── CubeRM.Identity.Infrastructure/      ← EF, Graph, interceptors
├── CubeRM.Identity.Api/                 ← HRD endpoint + claims-enrichment webhook
└── CubeRM.Identity.Provisioning/        ← reconciler CLI (see §5)
```

The important structural decision is **`CubeRM.SharedKernel.Auth` as a separately
versioned package**. With multiple internal applications, the alternative — each API
configuring `JwtBearer` itself — guarantees drift, and drift in token validation is a
security defect. One package, one `AddCubeAuthentication()` call, one place to fix a
validation bug across every service.

## 3.3 What each API writes

The entire integration surface for a product API is this:

```csharp
builder.Services
    .AddCubeAuthentication(builder.Configuration)   // JwtBearer, multi-issuer, claims mapping
    .AddCubeTenantIsolation();                      // MediatR behaviour + EF interceptor

app.UseAuthentication();
app.UseAuthorization();
app.UseCubeTenantContext();                         // populates ICubeTenantContext per request
```

Everything else — multi-issuer resolution, org deny-list checks, RLS session variable — is
inside the package.

## 3.4 Token validation: gateway *and* in-app

Both. They do different jobs and the in-app validation is not optional.

| | APIM | In-app (`JwtBearer`) |
| --- | --- | --- |
| Signature, `iss`, `aud`, `exp` | Yes | **Yes, independently** |
| `cube_org_id` present | Yes (cheap reject) | Yes |
| Org suspended deny-list | Yes (fast revocation) | Yes (authoritative) |
| Per-org rate limiting | Yes | No |
| Roles / permissions | No | Resolved from PostgreSQL, not from token |

**The API never trusts a header from APIM as proof of authentication.** APIM adds
`X-Cube-Org-Id` for logging and routing convenience only; the API derives org from the
validated token, never from the header. If the two disagree the request is rejected and an
alert fires — that mismatch means either a gateway bug or a bypass attempt.

This matters because App Service is reachable over the internet by default. The
network-level controls in [§4.7](04-apim-configuration.md) are necessary; independent
in-app validation is what makes them non-load-bearing.

## 3.5 Multi-issuer validation

A single authentication scheme with a dynamic issuer resolver, rather than N schemes.
N schemes means the `[Authorize]` attribute has to name a scheme, which means endpoint
code knows about issuer topology. It should not.

```csharp
options.TokenValidationParameters = new TokenValidationParameters
{
    ValidateIssuer = true,
    IssuerValidator = (issuer, token, parameters) =>
        registry.IsTrusted(issuer)
            ? issuer
            : throw new SecurityTokenInvalidIssuerException($"Untrusted issuer"),

    // Signing keys fetched per-issuer from that issuer's own JWKS, cached.
    IssuerSigningKeyResolver = (token, securityToken, kid, parameters) =>
        keyResolver.ResolveKeys(securityToken.Issuer, kid),

    ValidateAudience = true,
    ValidAudiences = options.ValidAudiences,
    ValidateLifetime = true,
    ClockSkew = TimeSpan.FromSeconds(30)   // not the 5-minute default
};
```

Three details that are easy to get wrong:

1. **`ClockSkew` defaults to five minutes.** That extends every token's usable life by five
   minutes past expiry, which directly weakens the offboarding story. Set it to 30 seconds.
2. **JWKS caching must be per-issuer** with independent refresh. A customer rotating their
   signing key must not invalidate our tenant's cached keys.
3. **`IssuerValidator` must not do I/O.** The registry is an in-memory cache refreshed on a
   timer from PostgreSQL; a database call inside token validation puts the identity
   database on every request's hot path.

## 3.6 Tenant context and the CQRS pipeline

### The abstraction

```csharp
public interface ICubeTenantContext
{
    bool     IsResolved { get; }
    Guid     OrgId      { get; }   // throws if !IsResolved — never silently returns Guid.Empty
    string   OrgSlug    { get; }
    Guid     UserId     { get; }
    string   Issuer     { get; }
    string   AuthMethod { get; }   // local | saml:{slug} | oidc:{slug} | entra:{tid}
    bool     IsSupportSession { get; }
    string?  SupportTicketRef { get; }
}
```

`OrgId` **throws** when unresolved rather than returning `Guid.Empty`. A default-valued
org id that flows into a query is the exact failure mode that produces a cross-tenant
read; making it unrepresentable is worth the exception.

### The enforcing behaviour

```csharp
public sealed class TenantIsolationBehaviour<TRequest, TResponse>
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct)
    {
        if (request is ITenantScopedRequest scoped)
        {
            if (!_tenant.IsResolved)
                throw new TenantContextMissingException(typeof(TRequest).Name);

            // A request carrying an explicit OrgId must match the token's org.
            if (scoped.OrgId != Guid.Empty && scoped.OrgId != _tenant.OrgId)
                throw new CrossTenantAccessException(typeof(TRequest).Name, scoped.OrgId, _tenant.OrgId);
        }
        return await next();
    }
}
```

And the invariant that makes it meaningful — an **architecture test**, run in CI:

```csharp
[Fact]
public void Every_command_and_query_is_either_tenant_scoped_or_explicitly_exempt()
```

Every `IRequest` must implement `ITenantScopedRequest` or carry `[TenantExempt]` with a
justification string. A new handler that is neither fails the build. This converts
"remember to scope your query" from a code-review habit into a compiler-adjacent guarantee.

### The database interceptor (L4)

```csharp
public sealed class OrgScopedConnectionInterceptor : DbConnectionInterceptor
{
    public override async Task<DbConnection> ConnectionOpenedAsync(...)
    {
        if (_tenant.IsResolved)
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT set_config('app.current_org_id', $1, false)";
            // parameterised — never string-concatenated
            await cmd.ExecuteNonQueryAsync(ct);
        }
        return connection;
    }
}
```

Two correctness requirements, both of which have bitten people:

1. **`set_config(..., false)` is session-scoped, and connections are pooled.** The setting
   must be cleared on connection return (`ConnectionDisposing`), or request N+1 can inherit
   request N's org on a recycled connection. This is the single highest-severity bug
   available in this design and it gets a dedicated test that hammers a small pool
   concurrently with two orgs.
2. The interceptor and EF global query filters are **belt and braces, not alternatives**.
   Query filters give good error messages and good SQL; RLS gives the guarantee.

## 3.7 Authorization, given the app database is authoritative

Roles are not in the token, so authorization is a per-request resolution:

```
Request → token validated → ICubeTenantContext resolved
        → IPermissionResolver.GetPermissionsAsync(userId, orgId)   [cached, 60s TTL]
        → requirement handler evaluates
```

Design notes:

- **Cache keyed `(userId, orgId)`, short TTL, explicitly invalidated** on permission change
  via a Service Bus message to all instances. A 60-second stale window on a permission
  *grant* is fine; on a *revoke* it is not, hence explicit invalidation.
- Permission checks live in **Application** as MediatR requirements, not as `[Authorize]`
  attributes on controllers. Authorization for a CQRS system belongs with the command, not
  with the transport.
- The org-level entitlement check (is this org licensed for this module) is a separate
  concern from the user-level permission check, and both are evaluated.

## 3.8 Where Durable Functions and Service Bus fit

Background work has no `HttpContext` and therefore no ambient tenant context. The failure
mode is a durable orchestration that processes org A's data under a context left over from
whatever ran previously.

**Rule: `org_id` is an explicit, mandatory part of every message and orchestration input,
and the worker establishes tenant context from the message before doing anything else.**

```csharp
public sealed record TenderIndexingMessage(Guid OrgId, Guid TenderId, string CorrelationId);

// In the handler, before any data access:
using var scope = _tenantScopeFactory.CreateScope(message.OrgId, reason: "service-bus:tender-indexing");
```

`CreateScope` sets `ICubeTenantContext` for the scope, which flows to the RLS interceptor
identically to an HTTP request. A worker that fails to create a scope gets the same
`TenantContextMissingException` — RLS returns zero rows, the job fails loudly, and nothing
crosses an org boundary silently.

Messages are also signed/validated for org consistency: a message whose `OrgId` does not
match the org of the entity it references is rejected and alerted.

## 3.9 Testing strategy

| Test | Asserts |
| --- | --- |
| `TenantIsolationBehaviourTests` | Missing context throws; mismatched explicit org throws |
| `RlsIsolationTests` (Testcontainers + real PostgreSQL) | Org A context sees zero org B rows, **including via `IgnoreQueryFilters()`** |
| `ConnectionPoolLeakageTests` | Concurrent two-org load over a 2-connection pool never leaks org context |
| `SchemaInvariantTests` | Every table with `org_id` has RLS enabled **and forced** |
| `ArchitectureTests` | Every `IRequest` is tenant-scoped or explicitly exempt; Application project has no reference to `ClaimsPrincipal` |
| `ClaimsMappingTests` | Tokens from each issuer archetype map to the correct org; unknown issuer rejected |

The Testcontainers-based RLS test is the one that substantiates the isolation claim in a
customer security questionnaire. It should be runnable on demand and its output
attachable to an audit response.
