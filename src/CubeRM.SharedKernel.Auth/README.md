# CubeRM.SharedKernel.Auth

The authentication and tenant-isolation plumbing every Cube RM API consumes. Published as
an internally versioned NuGet package so token validation is defined once rather than
re-implemented per service.

## Usage

```csharp
builder.Services
    .AddCubeAuthentication(builder.Configuration)
    .AddCubeTenantIsolation();

app.UseAuthentication();
app.UseAuthorization();
app.UseCubeTenantContext();   // after UseAuthentication
```

## Configuration

```json
{
  "CubeAuthentication": {
    "CiamAuthority": "https://cuberm.ciamlogin.com/00000000-0000-0000-0000-000000000000/v2.0",
    "CiamTenantId": "00000000-0000-0000-0000-000000000000",
    "ValidAudiences": [ "api://cube-tenderhub" ],
    "WorkforceAuthority": "https://login.microsoftonline.com/<cube-workforce-tid>/v2.0",
    "IdentityServiceBaseAddress": "https://identity.cuberm.internal/",
    "ClockSkew": "00:00:30",
    "IssuerRegistryRefreshInterval": "00:00:30"
  }
}
```

## What it gives you

| | |
| --- | --- |
| Multi-issuer JWT validation | Cube RM CIAM tenant, Cube RM workforce tenant (support), and per-customer Entra tenants (ADR-0004), all on one scheme |
| Claims to tenant context | `ICubeTenantContext`, the only identity abstraction handlers see |
| Authoritative suspension check | In-app deny-list, refreshed every 30 s to match the APIM cache |
| Gateway mismatch tripwire | Logs critical and rejects when APIM's `X-Cube-Org-Id` disagrees with the token |

## What it deliberately does not do

- **Roles and permissions.** Not in tokens (ADR-0002). Resolve from PostgreSQL per request.
- **Trust the gateway.** Every API validates independently; APIM can only ever reject more.
- **Silently default an organisation.** `ICubeTenantContext.OrgId` throws when unresolved.

## Scaffolding status

`CubeTenantContextMiddleware.ResolveCubeUserId` throws `NotImplementedException` — it is the
one intentional seam, pending the `identity.users` lookup. Everything else is complete in
shape but **has not been compiled** (no .NET SDK in the authoring environment). Treat it as
reviewed scaffolding: expect to fix using directives and package references on first build.
