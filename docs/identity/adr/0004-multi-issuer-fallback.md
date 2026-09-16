# ADR-0004 — Customer Entra tenants as additional trusted issuers

**Status:** Accepted · **Date:** 2026-09-16

## Context

Customers running their own Entra workforce tenant may not be federatable into our
External ID tenant. There are real-world reports of SAML federation from an External ID
CIAM tenant to a partner Entra tenant failing despite documentation, and of OIDC federation
between Microsoft tenants being blocked in some configurations. This is risk **R1**, and
Entra-based customers are likely a large share of our target market.

## Decision

**Our APIs validate tokens from multiple issuers.** A customer's own Entra tenant can be
registered in `identity.trusted_issuers` with a one-to-one mapping to a `cube_org_id`,
enforced by a unique constraint. Their users authenticate against their own tenant; our API
resolves the organisation from the issuer registry rather than from a token claim.

## Rationale

1. **It removes R1 from the critical path.** Federation working becomes an optimisation
   rather than a delivery dependency.
2. **It is arguably better for the customer.** Their Conditional Access, MFA and device
   compliance apply natively, which is a stronger story in a pharma security review than
   "we federate your IdP."
3. **It costs less in MAU.** Those users never authenticate against our tenant, so they are
   not our MAU ([§8.5](../08-mau-cost-model.md#85-the-trusted-issuer-cost-lever)). For a
   large customer this is material.
4. **Downstream code does not care.** Both paths produce an `ICubeTenantContext`. Handlers,
   RLS and authorization are identical.

## Consequences

- `OnTokenIssuanceStart` does not fire, so no `cube_org_id` claim. Org is resolved
  server-side from the registry — one extra cached lookup.
- The APIM deny-list cannot see these tokens; revocation runs through the issuer registry
  instead, with a matching 30-second cache TTL
  ([§6.3](../06-offboarding-incident.md#the-trusted-issuer-exception)).
- Cross-app SSO is not shared via our CIAM session cookie; each app authenticates against
  the customer tenant separately.
- A second integration pattern to support permanently.
- **The one-to-one issuer → org constraint is load-bearing.** It is what makes this path as
  isolated as the federated path: a token from a customer tenant can only ever resolve to
  that customer, because the database says so.

## Open question for Phase 0

Given the MAU benefit, should this be the **preferred** path for large Entra-based
customers rather than a fallback? Model it alongside R1 and decide explicitly.
