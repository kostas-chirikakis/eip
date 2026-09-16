# Cube RM Identity Provider Layer — Design & Implementation Plan

**Status:** Draft for review · **Owner:** Identity Architecture · **Target:** Phase 1, 20–30 enterprise customer organisations

---

## 1. What this document is

A complete design and build plan for the Cube RM Identity Provider layer, built on
**Microsoft Entra External ID (external tenant)**, serving multiple internal Cube RM
applications and 20–30 external customer organisations from pharma and medical device.

Two decisions are fixed inputs and are not revisited anywhere in this plan:

| Decision | Value |
| --- | --- |
| Identity provider | Microsoft Entra External ID |
| Tenant topology | **One** external tenant for all customer organisations |

Four further decisions were taken at design kickoff:

| Decision | Value | ADR |
| --- | --- | --- |
| CIAM product | External ID **external tenant** (not B2C, not workforce+guests) | [ADR-0001](adr/0001-single-external-tenant.md) |
| App topology | **Mixed** — SPAs with MSAL.js *and* server-rendered .NET behind a BFF | [ADR-0005](adr/0005-mixed-spa-and-bff.md) |
| Migration | **Greenfield** — no existing production user base to migrate | — |
| Authorization source of truth | **Application database** (PostgreSQL). Entra carries identity + org binding only | [ADR-0002](adr/0002-app-db-authoritative-authz.md) |

## 2. The design in one paragraph

Entra External ID is the **authentication authority** and nothing else. It proves *who*
a human is and stamps exactly one business-meaningful fact into the token: which customer
organisation they belong to. Everything else — roles, permissions, entitlements, org
lifecycle, domain routing, IdP configuration inventory — lives in our PostgreSQL identity
schema, which is the **authorization authority**. We deliberately own the home-realm
discovery layer ourselves rather than depending on External ID to route by email domain,
because that (a) is the part of the product most likely to differ from documentation,
(b) gives us one place to enforce per-org policy before a request ever reaches Entra, and
(c) turns onboarding customer #21 into a database row plus an idempotent Graph reconcile
instead of a portal click-through. Isolation between the 30 customers is not claimed on
the strength of the directory; it is enforced in depth, ending at PostgreSQL row-level
security, so that a bug in application code still cannot cross an organisation boundary.

## 3. The five load-bearing choices

These are the choices that, if reversed, force a redesign. Each has a dedicated ADR.

### 3.1 The application database is authoritative for authorization

Entra issues `cube_org_id` and nothing about roles. Roles, permissions and entitlements
are resolved in our CQRS pipeline from PostgreSQL.

**Why:** at 30 customers × N roles, directory groups explode combinatorially, every
permission change becomes a Graph write on the critical path, and tokens bloat toward the
groups-overage cliff. It also keeps customer admin delegation entirely out of the
directory, which is what makes §3.2 possible.

### 3.2 No customer ever holds an Entra directory role

Customer administrators do not have accounts in the Entra admin portal, do not hold
directory roles, and are not scoped by Administrative Units. They administer their
organisation exclusively through the **Cube RM Tenant Admin API**, which is org-scoped by
our own authorization layer and calls Microsoft Graph using a narrow application identity
that we control.

**Why:** this is the single strongest mitigation for the blast radius of the shared-tenant
model. Directory-level admin scoping in an external tenant is exactly the kind of control
that is easy to misconfigure once and hard to audit continuously. If no customer principal
can reach the directory at all, that entire class of cross-tenant exposure is designed out
rather than configured away. It also removes our dependence on Administrative Unit support
in external tenants, which is a live uncertainty (see [risk register](07-risk-register.md), R5).

### 3.3 We own home-realm discovery

A Cube RM–hosted `/auth/discover` endpoint takes an email address, resolves the domain
against `identity.org_domains` in PostgreSQL, applies org status policy, and *then* starts
the appropriate Entra authorize request with the correct IdP pinned.

**Why:** email-domain routing across a mixed population of federated and local-account
customers is the single most product-version-sensitive requirement in the brief. Owning
the routing layer makes the design correct whether or not External ID's native
domain-based routing behaves as documented, and it is where the offboarding kill switch
naturally lives. See [ADR-0003](adr/0003-own-the-hrd-layer.md).

### 3.4 Token enrichment happens at issuance, sourced from our database

An Entra **custom authentication extension** (`OnTokenIssuanceStart`) calls a Cube RM
endpoint during token issuance; we return `cube_org_id`, `cube_org_slug` and `cube_idp`
read from PostgreSQL. Directory extension attributes hold the same org binding as a
durable backstop, but the database is the source of truth.

**Why:** it means an organisation suspended in our database cannot be issued a
well-formed Cube RM token, even if directory state is stale. It fails closed.

**Cost:** this places a Cube RM HTTPS endpoint on the critical authentication path with a
low latency budget. That is a real availability coupling and is spiked early
(see [risk register](07-risk-register.md), R2), with a documented fallback to
directory-extension-only claims.

### 3.5 Customer Entra tenants can be trusted directly as additional issuers

Where a customer runs their own Entra ID workforce tenant and built-in federation proves
problematic, we do not fight it. Our APIs validate tokens from **multiple issuers**: the
Cube RM external tenant *and* an allowlist of customer workforce tenants, each pinned to a
`tid` that maps to exactly one `cube_org_id` in our issuer registry. Downstream of claims
mapping the two paths are indistinguishable.

**Why:** the brief flags Entra-to-Entra federation as a known real-world failure mode. A
large share of pharma and medical device organisations run Entra. Treating that as a
first-class supported path rather than a workaround removes the largest single delivery
risk from the critical path. See [ADR-0004](adr/0004-multi-issuer-fallback.md).

## 4. Document map

| # | Deliverable | Document |
| --- | --- | --- |
| 1 | Tenant isolation model | [01-tenant-isolation-model.md](01-tenant-isolation-model.md) |
| 2 | Auth flow diagrams | [02-auth-flows.md](02-auth-flows.md) |
| 3 | .NET / Clean Architecture integration | [03-dotnet-integration.md](03-dotnet-integration.md) |
| 4 | APIM configuration | [04-apim-configuration.md](04-apim-configuration.md) |
| 5 | Onboarding runbook & automation | [05-onboarding-runbook.md](05-onboarding-runbook.md) |
| 6 | Offboarding & incident response | [06-offboarding-incident.md](06-offboarding-incident.md) |
| 7 | Risk register, ordered by spike priority | [07-risk-register.md](07-risk-register.md) |
| — | MAU cost exposure model | [08-mau-cost-model.md](08-mau-cost-model.md) |

Code scaffolding lives in [`src/`](../../src), APIM policy in [`infra/apim`](../../infra/apim),
customer declarations in [`infra/customers`](../../infra/customers), and schema in
[`db/migrations`](../../db/migrations).

## 5. Phasing

| Phase | Content | Gate to exit |
| --- | --- | --- |
| **0 — Spikes** | R1–R4 from the risk register, in a throwaway external tenant | Federation path per customer archetype is known-good or known-bad, with evidence |
| **1 — Core** | Tenant schema + RLS, `CubeRM.SharedKernel.Auth`, one API behind APIM, local-account customers only | A local-account customer signs in and is provably isolated at the database layer |
| **2 — Federation** | HRD endpoint, SAML/OIDC customer federation, multi-issuer validation | Two federated customers live, one via built-in federation and one via the multi-issuer path |
| **3 — Scale-out** | Provisioning reconciler, onboarding pipeline, MAU telemetry, offboarding drill | Customer onboarded end-to-end by pipeline with zero portal interaction; offboarding drill passes |

Phase 0 is not optional and is not parallelisable with Phase 2 — the spike outcomes
determine what Phase 2 actually builds.

## 6. What this plan deliberately does not do

- **It does not put roles in tokens.** Ever. Tokens carry identity and org binding.
- **It does not create user accounts for machines.** Service-to-service uses app
  registrations and client credentials, which do not count toward MAU. See
  [08-mau-cost-model.md](08-mau-cost-model.md).
- **It does not trust the gateway alone.** APIM validates, and every API validates again
  independently. A compromised or bypassed gateway must not be sufficient.
- **It does not rely on directory configuration for the isolation guarantee.** The
  guarantee is enforced at the data layer and is testable in CI.
