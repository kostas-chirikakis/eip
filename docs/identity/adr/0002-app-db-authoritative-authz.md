# ADR-0002 — The application database is authoritative for authorization

**Status:** Accepted · **Date:** 2026-09-16

## Context

Roles and permissions could live in the directory (Entra app roles and groups, flowed as
token claims) or in our PostgreSQL database, resolved per request.

## Decision

**Entra issues identity and exactly one business claim — `cube_org_id`. Roles,
permissions and entitlements live in PostgreSQL and are resolved in the CQRS pipeline.**

## Rationale

1. **Combinatorics.** 30 customers × N roles × M resource scopes as directory groups is
   unmanageable, and group-based claims hit the token overage cliff, which forces a Graph
   call on every request anyway — the worst of both models.
2. **Change latency.** A permission change becoming a Graph write puts directory
   availability on the path of an ordinary product operation.
3. **It enables the no-customer-directory-access rule.** If roles were in the directory,
   customer admins would need directory write access to manage their own users. Keeping
   authorization in our database is what makes
   [§1.4](../01-tenant-isolation-model.md#14-admin-role-scoping) possible, and that is the
   strongest mitigation for the shared-tenant blast radius.
4. **Tender-management authorization is domain-shaped.** Access to a tender workspace, a
   submission, a document — these have no sensible directory representation. Splitting
   coarse roles into the directory and fine-grained into the database means two models to
   reason about at every review.

## Consequences

- Tokens stay small (see R12) and stable.
- Permission resolution is a cached per-request lookup with a 60-second TTL and explicit
  invalidation on revoke via Service Bus.
- We build the Tenant Admin API. This is work we would have needed regardless, since
  product-level permissions have no directory equivalent.
- Directory audit logs do **not** tell the whole authorization story. Our own audit log is
  the record of record, and must meet the standard a pharma customer's auditor expects.
