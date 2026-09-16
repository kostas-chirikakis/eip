# CubeRM.Identity.Provisioning

Declarative reconciler for customer identity configuration. Turns
`infra/customers/{slug}.yaml` into state in the shared Entra tenant, our PostgreSQL
identity schema, and APIM.

**The principle it exists to enforce:** a customer's identity configuration is a reviewed
file in git, and a machine applies it. No human touches the production Entra tenant.

## Commands

```bash
# Read-only. Safe against production. Posted as a PR comment.
cube-identity plan  --customer acme-pharma --tenant non-prod

# Idempotent. Re-running is a no-op.
cube-identity apply --customer acme-pharma --tenant prod --confirm

# Reconcile-all, report divergence, write nothing. Runs nightly.
cube-identity drift --tenant prod

# Emergency suspension (docs/identity/06-offboarding-incident.md §6.3).
# Cuts 1-3 are single-row updates in OUR database — no Entra access required,
# and structurally incapable of affecting another customer.
cube-identity suspend --org acme-pharma --reason "IdP compromise, INC-2026-0417" --confirm

# Proves the blast radius was contained. The question a responder needs answered in
# seconds is "did I just break everyone?"
cube-identity verify-isolation --suspended-org acme-pharma --sample-orgs 5
```

## Why a .NET reconciler rather than Terraform

The `azuread` Terraform provider's coverage of External ID **external tenant** identity
providers and user flows is thin and lags the product. Betting the onboarding path on
provider coverage puts us one unsupported resource away from falling back to manual portal
work — the exact outcome this tool exists to prevent.

Azure *infrastructure* (APIM, App Service, networking) stays in Bicep/Terraform. This
replaces neither.

## Graph permissions

Application permissions, minimised. Authenticated by a **federated credential from the CI
workload identity** — no client secret exists to leak or rotate, and it is usable only from
a pipeline run on a protected branch.

| Permission | Why |
| --- | --- |
| `IdentityProvider.ReadWrite.All` | Create and update customer IdPs |
| `User.ReadWrite.All` | Provision users, write extension attributes |
| `Group.ReadWrite.All` | Operational groups |
| `Application.Read.All` | Read app registrations for user-flow association |
| `AuditLog.Read.All` | Post-apply verification |

These are tenant-wide because Graph offers no per-customer scoping for identity provider
management. That is residual risk **RT-7**, stated plainly rather than papered over — it is
a real cost of the single-tenant topology, mitigated by the credential being CI-only and
every write being audited.

## Safety properties

| Property | How |
| --- | --- |
| `plan` never writes | Reconcilers' `PlanAsync` is read-only by contract |
| Re-runnable | Every resource is upsert-by-natural-key; partial failures resume |
| Deletions gated | `--confirm-destructive`, supplied by CI only for an offboarding-labelled PR |
| Correct ordering | `ReconciliationOrchestrator.Order`; domain routing is applied **last**, because it is what makes a customer live |
| Non-prod first | CD applies to non-prod and runs a synthetic sign-in before prod |

## Scaffolding status

`Model/` and `Reconcilers/` define the shape and the safety contract. The Graph-calling
implementations of `IResourceReconciler` are the build work. Nothing here has been
compiled — there was no .NET SDK in the authoring environment.
