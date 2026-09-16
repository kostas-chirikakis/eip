# 1. Tenant Isolation Model

How 20–30 customer organisations are partitioned inside **one** Entra External ID
external tenant, and why the resulting isolation claim survives a pharma security review.

---

## 1.1 The core assertion

> Isolation between customer organisations is enforced at four independent layers. No
> single misconfiguration at any one layer results in cross-organisation data exposure.

| Layer | Mechanism | What it stops | Fails how |
| --- | --- | --- | --- |
| L1 Directory | Org binding on every user; no customer-held directory roles; directory read denied to end-user tokens | A customer enumerating another customer's users | Closed — no org claim, no token |
| L2 Edge (APIM) | `validate-jwt` + org deny-list + per-org rate limiting | Suspended orgs; noisy-neighbour capacity exhaustion | Closed — request rejected at gateway |
| L3 Application | MediatR `TenantIsolationBehaviour`; EF Core global query filters | Handler code that forgets to scope a query | Closed — request throws before handler runs |
| L4 Database | PostgreSQL row-level security keyed on session GUC | Everything above it being wrong simultaneously | Closed — zero rows returned |

L4 is the layer that makes the claim defensible. L1–L3 are all *code and configuration we
wrote*, and therefore all fallible. L4 is enforced by the database engine against a
connection role that has no `BYPASSRLS`, so a SQL-level mistake in application code
returns nothing rather than returning another customer's tender data.

---

## 1.2 Identifier and naming conventions

### 1.2.1 Organisation identity

Every customer organisation has exactly three identifiers. They are established at
onboarding and **never change**.

| Identifier | Form | Example | Used for |
| --- | --- | --- | --- |
| `org_id` | UUID v7 | `0192f4c1-…-a3e2` | Primary key everywhere; the value in `cube_org_id` claim |
| `slug` | `^[a-z][a-z0-9-]{2,30}$` | `acme-pharma` | Human-readable; Entra object naming; log correlation |
| `display_name` | free text | `Acme Pharmaceuticals GmbH` | UI only. Never an identifier. |

`slug` is immutable because it is embedded in Entra object display names, and renaming
directory objects across 30 customers is exactly the kind of manual drift this design
exists to prevent. If a customer rebrands, `display_name` changes and `slug` does not.

### 1.2.2 Entra object naming

Everything we create in the shared tenant is prefixed so that ownership is unambiguous at
a glance in the portal, and so that an orphan-detection job can find objects that no
longer map to a live organisation.

| Object type | Convention | Example |
| --- | --- | --- |
| Federated identity provider | `idp-{slug}-{saml\|oidc}` | `idp-acme-pharma-saml` |
| Group (operational only, never authz) | `org.{slug}.members` | `org.acme-pharma.members` |
| Emergency disable group | `org.{slug}.suspended` | `org.acme-pharma.suspended` |
| App registration (per Cube RM app, **not** per customer) | `cube-{app}-{env}` | `cube-tenderhub-prod` |
| Custom auth extension | `cube-claims-enrichment-{env}` | `cube-claims-enrichment-prod` |

**There is no per-customer app registration.** This is a requirement from the brief and it
is also the right call: per-customer app registrations would multiply redirect URI
management, consent, and secret rotation by 30, and would leak customer identity into the
`aud` claim where it does not belong.

### 1.2.3 User principal naming

Local-account users are created with their real email as the sign-in identity. We do
**not** namespace emails per organisation (e.g. `user+acme@…`), because:

- it breaks the customer's own mail routing and password-reset expectations, and
- uniqueness is already guaranteed — a given email address belongs to exactly one
  organisation by definition of the domain routing table.

The one case that needs care is a **consultant working for two customers**. See §1.7.

---

## 1.3 Attribute and claim schema

### 1.3.1 Directory extension attributes

Registered against the claims-enrichment app registration, so they appear as
`extension_{appIdNoHyphens}_{name}`.

| Attribute | Type | Mutability | Purpose |
| --- | --- | --- | --- |
| `cubeOrgId` | String | Write-once at provisioning | Durable org binding; survives database restore ordering |
| `cubeOrgSlug` | String | Write-once | Human-readable correlation in directory/audit logs |
| `cubeUserType` | String | Mutable | `member` \| `org_admin` \| `cube_support` |
| `cubeProvisionedAt` | String (ISO 8601) | Write-once | Orphan detection and onboarding audit |

These are a **backstop, not the source of truth**. The authoritative binding is
`identity.users.org_id` in PostgreSQL. The directory copy exists so that:

1. directory-side tooling and audit logs are legible without a database join, and
2. if the claims-enrichment extension is unavailable, we have a documented degraded mode
   (see [risk register](07-risk-register.md), R2) that maps claims from directory
   attributes instead.

Only the provisioning service principal holds write permission on these attributes. No
user-delegated token can modify them.

### 1.3.2 Token claims

The tokens our APIs accept carry a deliberately small claim set.

```jsonc
{
  // Standard — issued by Entra
  "iss":   "https://cuberm.ciamlogin.com/<tenant-id>/v2.0",
  "aud":   "api://cube-tenderhub",
  "sub":   "<pairwise, per-app>",          // NOT a durable user key
  "oid":   "<stable object id in our tenant>", // durable user key
  "tid":   "<our external tenant id>",
  "email": "j.smith@acme-pharma.com",
  "exp":   1770000000,

  // Cube RM — injected by the claims-enrichment extension
  "cube_org_id":   "0192f4c1-...-a3e2",
  "cube_org_slug": "acme-pharma",
  "cube_idp":      "saml:acme-pharma",     // local | saml:{slug} | oidc:{slug} | entra:{tid}
  "cube_ver":      "1"                      // claim schema version
}
```

**What is deliberately absent:** roles, permissions, entitlements, group memberships,
licence tiers. All resolved server-side per request from PostgreSQL.

Three rules govern this schema:

1. **`oid`, not `sub`, is the durable user key.** `sub` is pairwise per application in
   Entra; the same human presents a different `sub` to the web app and to the API. Keying
   user records on `sub` is a defect that surfaces only once you add your second
   application, which is precisely our scenario.
2. **`(issuer, oid)` is the composite user key**, not `oid` alone. Under the multi-issuer
   path (§1.6) an `oid` from a customer's own Entra tenant is only unique within that
   tenant. `identity.users` is keyed accordingly.
3. **`cube_ver` gates forward compatibility.** When the claim set changes, the version
   increments and `CubeClaimTypes` handles both until all tokens have rolled over.

---

## 1.4 Admin role scoping

This is the highest-consequence area of the single-tenant model and it is handled by
removing the problem rather than scoping it.

### 1.4.1 Three principal classes, and what each may touch

| Class | Holds directory role? | Reaches Entra portal? | Administers via |
| --- | --- | --- | --- |
| **Customer end user** | No | No | Nothing |
| **Customer org admin** | **No** | **No** | Cube RM Tenant Admin API, org-scoped by L3/L4 |
| **Cube RM operator** | Yes, via PIM, time-bound | Yes, with Conditional Access + phishing-resistant MFA | Entra portal / Graph |

A customer org admin can invite users, assign Cube RM roles, and configure their own
federation metadata — all through **our** API. Under the hood, our Tenant Admin API calls
Graph with an application identity, after our own authorization layer has confirmed that
the caller's `cube_org_id` matches the org being modified.

### 1.4.2 Why not Administrative Units

Administrative Units are the documented Entra mechanism for scoping directory admin to a
subset of users, and they would be the obvious fit if customer admins needed directory
access. Two reasons we do not depend on them:

1. **AU support in external tenants is a live uncertainty** and would need validation
   before being load-bearing (risk register R5).
2. Even where AUs work, they scope *directory* operations. Our customer admins need to
   perform *product* operations (assign a Cube RM role, grant access to a tender
   workspace) that have no directory representation at all. We would end up building the
   Tenant Admin API regardless, and then maintaining two parallel admin surfaces with two
   different authorization models. One surface is strictly safer.

### 1.4.3 Cube RM operator access

The operator population is the residual blast radius. Controls:

- Directory roles assigned **only** through Privileged Identity Management, time-bound,
  with approval, never standing.
- Conditional Access requiring phishing-resistant MFA and compliant device for any
  directory role activation.
- **Two break-glass accounts**, excluded from CA, credentials split and held in a physical
  safe, monitored by an alert that fires on any sign-in.
- Every tenant-level configuration change flows through the provisioning pipeline
  (§[onboarding](05-onboarding-runbook.md)) so it is peer-reviewed in a pull request
  before it touches 30 customers at once.

The last point is the important one. **The realistic blast-radius event in a shared tenant
is not a breach; it is a well-intentioned operator changing a tenant-wide setting on a
Friday afternoon.** Making configuration a reviewed, diffable artifact is the mitigation.

### 1.4.4 Cube RM support impersonation

Support staff need to reproduce a customer issue. They must **not** get accounts in the
external tenant (see [MAU model](08-mau-cost-model.md) — every support account is a
billable MAU, and it is also a standing cross-org credential).

Instead: support staff authenticate against the Cube RM **workforce** tenant. Our APIs
accept that workforce issuer as a third trusted issuer, mapping it to a
`cube_support` principal with **no** `cube_org_id`. An explicit, audited, time-boxed
impersonation grant in `identity.support_sessions` is what supplies the org scope for the
duration of a support session, and every request made under it is tagged in the audit log.

```
Support engineer → workforce tenant token (no cube_org_id)
                 → POST /support/sessions { org_id, ticket_ref, justification }
                 → 30-minute scoped grant, written to identity.support_sessions
                 → subsequent requests resolve org from the grant, not from the token
                 → every row access logged with ticket_ref
```

---

## 1.5 Federated IdP partitioning

Each customer that federates gets its own identity provider object in the shared tenant.
The isolation concern is real and specific: **can customer A's users be routed to, or
authenticate through, customer B's IdP?**

### 1.5.1 The threat

If the sign-in experience presents a list of configured IdP buttons, then in a shared
tenant that list is potentially *every customer's* IdP. A user at Acme could click
"Northwind SSO", authenticate against Northwind's directory, and arrive holding a token.
Whether that token carries Northwind's `cube_org_id` depends entirely on how our
enrichment logic resolves org — and if it resolves from the IdP rather than from a
pre-established user record, that is a cross-tenant breach.

### 1.5.2 The mitigations, in order of strength

1. **Never present an IdP list.** Our HRD endpoint (§[ADR-0003](adr/0003-own-the-hrd-layer.md))
   resolves the email domain first and pins exactly one IdP in the authorize request. The
   generic sign-in page with buttons is never reached.
2. **Org is resolved from the pre-existing user record, never from the IdP.** The
   claims-enrichment extension looks up `(issuer, oid)` in `identity.users`. If there is
   no record, **no `cube_org_id` is issued** and the token is rejected downstream. An
   unexpected authentication path therefore yields an unusable token rather than a
   mis-scoped one.
3. **Assert the IdP matches the org.** The enrichment endpoint compares the authenticating
   IdP against `identity.org_identity_providers` for the resolved org. A mismatch is a
   hard failure and a P1 security alert, not a warning.
4. **Domain ownership is verified before federation is enabled.** A customer proves
   control of `acme-pharma.com` (DNS TXT record) before we will route that domain to their
   IdP. Otherwise customer B can claim customer A's domain and harvest their sign-ins.

Mitigation 2 is the load-bearing one, and it is why **just-in-time user provisioning on
first federated sign-in is disabled by default**. JIT provisioning plus a shared tenant
plus a mis-pinned IdP is the exact combination that produces a silent cross-org account.
Customers who want JIT get it per-org, gated on verified domain ownership, and the JIT
path derives org from the *verified email domain*, never from the IdP that asserted it.

---

## 1.6 The multi-issuer path

For customers on the "trusted additional issuer" path (see
[ADR-0004](adr/0004-multi-issuer-fallback.md)), isolation works identically, with one
extra binding:

```
identity.trusted_issuers
  issuer_url          text primary key   -- https://login.microsoftonline.com/{tid}/v2.0
  org_id              uuid not null      -- exactly one org. Never many.
  entra_tenant_id     uuid not null
  allowed_audiences   text[] not null
  status              text not null
```

The `issuer → org_id` mapping is **one-to-one and enforced by a unique constraint**. An
issuer cannot serve two organisations. This is what makes the multi-issuer path as strong
as the federated path: a token from Acme's Entra tenant can only ever resolve to Acme,
because that is a database constraint rather than a claims-mapping decision.

---

## 1.7 The multi-org human

A consultant contracted to both Acme and Northwind is a genuine scenario in Life Sciences
tender work, and it is where naive single-tenant designs break.

**Rule: one identity, N explicit memberships, exactly one active org per token.**

- `identity.users` holds the human once, keyed `(issuer, subject_oid)`.
- `identity.org_memberships` holds `(user_id, org_id, status)` rows — one per org.
- The token carries **one** `cube_org_id`, chosen at sign-in via an org-selection step
  when more than one active membership exists.
- Switching org requires a **new token**, not a claim rewrite. There is no in-session org
  switch, because an in-session switch means a live token whose org scope is ambiguous.

This keeps L3/L4 enforcement simple: at any instant, a request has exactly one org, and
RLS has exactly one value to filter on.

---

## 1.8 Data-layer enforcement (L4)

Every tenant-owned table carries `org_id uuid not null` and an RLS policy:

```sql
alter table tenders enable row level security;
alter table tenders force row level security;

create policy tenders_org_isolation on tenders
  using (org_id = current_setting('app.current_org_id', true)::uuid);
```

The application connects as `cube_app`, a role with **no** `BYPASSRLS` and **no**
table ownership. `app.current_org_id` is set per transaction by a `DbConnection`
interceptor (see [.NET integration](03-dotnet-integration.md) §3.6) from
`ICubeTenantContext`, and cleared on connection return.

Three properties follow, and all three are worth stating explicitly to a customer's
security reviewer:

1. An application bug that omits a `WHERE org_id = …` clause returns **zero** rows, not
   another customer's rows.
2. `IgnoreQueryFilters()` — which developers legitimately need for admin queries — cannot
   escape isolation, because RLS is below EF Core entirely.
3. A SQL injection that reaches the database still executes under RLS.

`force row level security` matters: without it, the table owner bypasses the policy, and
migrations commonly run as owner. The migration role and the application role are separate
for exactly this reason.

### 1.8.1 Testing the claim

Isolation is a **CI-enforced invariant**, not a design intention:

- A schema test asserts every table carrying `org_id` has RLS enabled and forced. New
  tables fail the build until a policy exists.
- An integration test seeds two orgs, sets `app.current_org_id` to org A, and asserts org
  B's rows are invisible across every repository method — including those using
  `IgnoreQueryFilters()`.
- A test asserts that a request with no org context **throws** rather than defaulting to
  unscoped.

See [`tests/`](../../tests) for the scaffolded shape of these.

---

## 1.9 Residual risks of the single-tenant model

Stated plainly, with mitigations. These do not argue against the decision; they are what
the decision costs and how that cost is contained.

| # | Risk | Blast radius | Mitigation |
| --- | --- | --- | --- |
| RT-1 | Tenant-wide setting misconfigured (CA policy, user flow, attribute) | **All 30 customers at once** | All tenant config through reviewed PR + reconciler; no manual portal changes in prod; config drift detection job |
| RT-2 | One customer's federated IdP metadata change breaks sign-in | That customer only, *if* IdP is correctly pinned | HRD pins one IdP; per-org synthetic sign-in canary alerts within 5 min |
| RT-3 | Cross-org routing via IdP button list | Potentially any org | Never present IdP list; org resolved from user record not IdP (§1.5.2) |
| RT-4 | Operator error deletes wrong customer's objects | One customer, recovery is manual | Reconciler `plan` requires explicit confirm for deletes; soft-delete first, 30-day hard-delete lag |
| RT-5 | Directory enumeration by an end user | All users in tenant | Deny directory read to user-delegated tokens; **verify by test**, do not assume default (risk register R4) |
| RT-6 | Noisy neighbour — one customer exhausts API capacity | All customers | Per-org `rate-limit-by-key` at APIM ([APIM config](04-apim-configuration.md) §4.5) |
| RT-7 | Compromise of the provisioning service principal | All 30 customers | Federated credential (no secret), workload identity from CI only, Graph permissions minimised to the exact set in §5.4, all writes audited |
| RT-8 | Shared user-flow change alters sign-up UX for all | All customers | Per-org branding only via supported per-app branding; UX changes staged in non-prod tenant first |

RT-1 and RT-7 are the ones genuinely created by the single-tenant choice. Both are
addressed by the same principle: **there are no manual changes to the production tenant.**
Every change is a reviewed, versioned, idempotent artifact applied by a machine identity
that CI holds and humans do not.
