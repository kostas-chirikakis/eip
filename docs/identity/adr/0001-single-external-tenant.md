# ADR-0001 — Single Entra External ID external tenant for all customers

**Status:** Accepted (fixed input) · **Date:** 2026-09-16

## Context

Cube RM needs identity for 20–30 enterprise customer organisations in Life Sciences.
Microsoft Entra External ID is the chosen provider and a single external tenant serves all
customers. Both are fixed inputs to this design and are not re-litigated here.

The remaining decision within that constraint was **which** External ID product surface:
the external tenant (current CIAM), Azure AD B2C (legacy), or a workforce tenant with B2B
guests.

## Decision

**External ID external tenant.**

## Rationale

| Option | Verdict |
| --- | --- |
| **External tenant** | Current, supported CIAM product. Custom authentication extensions give us DB-sourced claims. Purpose-built for exactly this customer-identity shape. |
| Azure AD B2C | Would give richer home-realm discovery via Identity Experience Framework custom policies — genuinely useful for requirement 3. But it is closed to new tenants and in maintenance. Building a new platform on it is a known dead end. |
| Workforce + B2B guests | Different isolation model, different MAU accounting, and guest-based topology is a poor fit for customers who want local email/password accounts. |

The B2C trade-off is real and worth naming: we give up IEF custom policies, which is why
[ADR-0003](0003-own-the-hrd-layer.md) moves home-realm discovery into our own code. That is
a direct consequence of this choice, not an unrelated design preference.

## Consequences

**Accepted costs of the single-tenant topology** (see
[§1.9](../01-tenant-isolation-model.md#19-residual-risks-of-the-single-tenant-model) for
full treatment with mitigations):

- One password and MFA policy for all customers (RT-8, R11). Contractual, not fixable.
- A tenant-wide misconfiguration affects all 30 customers at once (RT-1, R10). Mitigated
  by making all configuration a reviewed, machine-applied artifact.
- The provisioning service principal necessarily holds tenant-wide Graph permissions
  (RT-7), because Graph offers no per-customer scoping for identity providers.
- All customers' IdP objects coexist, creating a cross-org routing surface (RT-3) that
  [§1.5](../01-tenant-isolation-model.md#15-federated-idp-partitioning) addresses.

**Gained:** one tenant to operate, one set of app registrations, no per-customer tenant
provisioning, and a single point of monitoring and audit.
