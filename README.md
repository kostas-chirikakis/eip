# Cube RM — Identity Provider Layer

Design and implementation plan for the Cube RM identity layer on **Microsoft Entra
External ID**, serving multiple internal applications and 20–30 enterprise customer
organisations in pharma and medical devices from a **single external tenant**.

> **Start here:** [`docs/identity/00-overview.md`](docs/identity/00-overview.md)

---

## The design in three sentences

Entra External ID is the authentication authority and issues exactly one business claim —
which customer organisation a user belongs to. Everything else (roles, permissions, domain
routing, org lifecycle) lives in PostgreSQL, which is the authorization authority. Isolation
between customers is enforced in depth across four independent layers, ending at PostgreSQL
row-level security, so an application bug cannot cross an organisation boundary.

## Repository layout

| Path | Contents |
| --- | --- |
| [`docs/identity/`](docs/identity) | The design. Nine documents plus five ADRs. |
| [`src/CubeRM.SharedKernel.Auth/`](src/CubeRM.SharedKernel.Auth) | Shared package every Cube RM API consumes — multi-issuer token validation, claims mapping, tenant context |
| [`src/CubeRM.Identity.*/`](src) | The identity service: Domain / Application / Infrastructure / Api, in Clean Architecture layers |
| [`src/CubeRM.Identity.Provisioning/`](src/CubeRM.Identity.Provisioning) | Declarative reconciler — the onboarding and offboarding tool |
| [`infra/apim/policies/`](infra/apim/policies) | Gateway policy, validated as well-formed XML in CI |
| [`infra/customers/`](infra/customers) | One YAML declaration per customer, plus its JSON schema |
| [`db/migrations/`](db/migrations) | Identity schema and row-level security policies |
| [`tests/sql/`](tests/sql) | **Executable proof** of the isolation claim |
| [`scripts/`](scripts) | CI gates |

## Documents

| # | Deliverable |
| --- | --- |
| [00](docs/identity/00-overview.md) | Overview, load-bearing decisions, phasing |
| [01](docs/identity/01-tenant-isolation-model.md) | Tenant isolation model — naming, claims, admin scoping, residual risks |
| [02](docs/identity/02-auth-flows.md) | Auth flows (Mermaid): internal SSO, federated, local, domain routing, multi-issuer, claims enrichment |
| [03](docs/identity/03-dotnet-integration.md) | .NET / Clean Architecture integration |
| [04](docs/identity/04-apim-configuration.md) | APIM: gateway vs in-app validation |
| [05](docs/identity/05-onboarding-runbook.md) | Onboarding runbook and automation |
| [06](docs/identity/06-offboarding-incident.md) | Offboarding and incident response |
| [07](docs/identity/07-risk-register.md) | Risk register, ordered by spike priority |
| [08](docs/identity/08-mau-cost-model.md) | MAU cost exposure |

## Before building anything

Two spikes block the design and are independent — **run them in parallel from day one**:

| | Spike | Why it blocks |
| --- | --- | --- |
| **R1** | Entra-to-Entra federation | Large pharma organisations are disproportionately on Entra. If federation is blocked and we found out after signing, we have promised an integration we cannot deliver. [ADR-0004](docs/identity/adr/0004-multi-issuer-fallback.md) is the designed fallback. |
| **R2** | `OnTokenIssuanceStart` custom auth extension | It is the mechanism for the org claim, and it sits on the critical auth path for every customer. |

Full detail and the remaining eleven risks: [risk register](docs/identity/07-risk-register.md).

## Verification status

Everything below was executed in the authoring environment, not merely written.

| Check | Result |
| --- | --- |
| RLS cross-tenant isolation, PostgreSQL 16.13 | **7/7 pass** — see [`tests/sql/README.md`](tests/sql/README.md) |
| Migrations `0001`–`0003` apply cleanly | Pass |
| Schema invariants (no `BYPASSRLS`, RLS forced on every `org_id` table) | Pass |
| Mermaid diagrams parse | 7/7 |
| APIM policy XML well-formed | 4/4 |
| Customer declarations valid, no domain collisions | 3/3 |
| Validator rejects domain collision, `selfServiceSignUp`, unverified-domain JIT | Pass |

| .NET solution builds, Release, warnings-as-errors | **6/6 projects, 0 warnings** |

**What building it found.** Three defects, one of which mattered: the R7 connection-pool
guard was hooked to `ConnectionDisposing` alone, which does not fire when Npgsql returns a
connection to the pool. See R7 in [the risk register](docs/identity/07-risk-register.md).
The other two were incomplete `<param>` documentation.

`src/` now compiles, but **compiling is not working**. There are no unit tests, no host
(`CubeRM.Identity.Api` is a library of endpoint definitions with no `Program.cs`), and one
seam is intentionally unimplemented and marked:
`CubeTenantContextMiddleware.ResolveCubeUserId`.

## Running the checks

```bash
# Customer declaration gates
python3 scripts/validate-customers.py

# Isolation proof — MUST connect as cube_app; a superuser bypasses RLS entirely
createdb cubeidentity
psql -d cubeidentity -f db/migrations/0001_identity_core.sql
psql -d cubeidentity -f db/migrations/0002_rls_policies.sql
psql -d cubeidentity -f db/migrations/0003_schema_invariant_check.sql
psql -d cubeidentity -f tests/sql/rls_isolation_seed.sql
psql -U cube_app -d cubeidentity -f tests/sql/rls_isolation_test.sql
```
