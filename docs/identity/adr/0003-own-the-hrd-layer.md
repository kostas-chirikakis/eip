# ADR-0003 — Cube RM owns home-realm discovery

**Status:** Accepted · **Date:** 2026-09-16

## Context

Requirement 3 needs email-domain routing between federated customers and local-account
customers, without a per-customer app registration. External ID external tenants offer
some native domain-based routing, but its behaviour is one of the least certain parts of
this design (risk **R3**), and unlike B2C there is no Identity Experience Framework to
customise it.

## Decision

**A Cube RM-hosted `/auth/discover` endpoint owns routing.** It resolves email domain →
organisation → routing mode from PostgreSQL, applies org status policy, and returns a
pre-built authorize URL with exactly one identity provider pinned.

## Rationale

1. **It makes the design robust to R3's outcome.** If native routing works, we use it
   underneath our endpoint. If it does not, the contract with our apps is unchanged. We
   are not blocked on a product behaviour we cannot control.
2. **It is where the kill switch belongs.** Org suspension is evaluated here, from our own
   database, before Entra is contacted — giving immediate effect on new sign-ins with no
   directory change ([§6.2](../06-offboarding-incident.md#62-the-kill-chain--why-this-is-fast)).
3. **It prevents the IdP picker.** Never presenting a list of every customer's IdP is the
   first mitigation for cross-org routing ([§1.5.2](../01-tenant-isolation-model.md#152-the-mitigations-in-order-of-strength)).
4. **It makes onboarding declarative.** Routing is a database row written by the
   reconciler, not a portal configuration.
5. **It controls the enumeration surface.** We decide what an unknown domain returns. The
   answer is "looks exactly like a local-account domain."

## Consequences

- A new unauthenticated public endpoint, requiring rate limiting, enumeration-resistant
  responses, and monitoring for domain probing.
- The domain → org mapping is a hot path; cached in-process with short TTL.
- We own the correctness of routing, including the domain-collision check that CI enforces
  at onboarding ([§5.3](../05-onboarding-runbook.md#53-the-pipeline)).
- One more service to keep available — though it is small, read-only and cacheable.
