# ADR-0005 — Support both SPA and BFF token topologies, converge on BFF

**Status:** Accepted · **Date:** 2026-09-16

## Context

Cube RM has both SPAs (MSAL.js, tokens in the browser) and server-rendered .NET
applications. Cross-application SSO must work across both.

## Decision

**Support both. Treat BFF as the target state and SPA-with-MSAL.js as supported-but-legacy.
Do not rely on hidden-iframe `ssoSilent` for cross-app SSO in either case.**

## Rationale

1. **Hidden-iframe silent auth depends on third-party cookies**, which modern browsers
   restrict by default. Cross-app SSO built on it degrades unpredictably by browser and
   by user setting — the worst kind of failure to support.
2. **Full-page redirect with `prompt=none`** uses a first-party cookie and is unaffected.
   Visually a flash; functionally reliable. This is the interim default for SPAs.
3. **BFF keeps tokens out of the browser entirely**, which is materially easier to defend
   in a pharma security review than `localStorage` or in-memory tokens.
4. **BFF gives us a killable session.** A server-side session can be revoked immediately;
   a token already in a browser cannot. This matters directly to
   [§6](../06-offboarding-incident.md).

## Consequences

- Two token-acquisition patterns to document and support during convergence.
- `CubeRM.SharedKernel.Auth` is agnostic — both produce a bearer token our APIs validate
  identically.
- Session lifetime semantics differ between the two and must be documented; a BFF session
  and an SPA token do not expire alike.
- New Cube RM applications should be BFF from the start. Existing SPAs migrate when they
  are next substantially worked on, not as a dedicated project.
