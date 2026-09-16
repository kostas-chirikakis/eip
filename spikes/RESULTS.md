# Phase 0 — Results

**The single decision table.** Phase 2 scope, the onboarding decision gate in
`docs/identity/05-onboarding-runbook.md` §5.6, and the sales conversation all read from
this file. A spike is not finished until its row is filled in.

> Status: **not started** — no spike has been run yet.

---

## R1 · Entra-to-Entra federation

| | |
| --- | --- |
| Run on | _date_ |
| By | _who_ |
| Evidence | `spikes/evidence/r1_entra_federation/<run>/` |

### Attempt outcomes

| Attempt | Outcome | Error code | Graph request-id |
| --- | --- | --- | --- |
| A0 control | | | |
| A1 OIDC partner Entra | | | |
| A2 OIDC beta | | | |
| A3 SAML verified domain | | | |
| A4 SAML unverified domain | | | |

### The decision table this produces

Fill this in — it is what the CSM reads before promising anything.

| Customer archetype | Supported mode | Caveats |
| --- | --- | --- |
| Own Entra tenant, email domain verified there | | |
| Own Entra tenant, domain not verified there | | |
| Non-Entra SAML IdP (ADFS, Okta, Ping) | | |
| Non-Entra OIDC IdP | | |
| No SSO wanted | `local` | Invitation-only; tenant-wide password policy applies |

### Consequences

- [ ] If federation is blocked for Entra customers, `trusted-issuer` is the **primary** path, not a fallback — update ADR-0004's status.
- [ ] Re-run the MAU model: trusted-issuer users are not our MAU, which may make that path preferable even where federation works.
- [ ] Until this table is filled in, **no federation date is quotable to a customer.**

---

## R2 · Custom authentication extension

| | |
| --- | --- |
| Run on | _date_ |
| Evidence | `spikes/evidence/r2_token_issuance/<run>/` |

| Question | Answer | Consequence |
| --- | --- | --- |
| Largest delay that still enriched the token | _ms_ | Design to half of it |
| Our p99 handling budget | _ms_ | |
| Fires on refresh-driven issuance? | yes / no | If **no**, a suspended org keeps working until the refresh token expires and the APIM deny-list is the only fast revocation |
| A 500 blocks sign-in, or degrades it? | blocks / degrades | If **blocks**, this is a hard availability dependency for every customer — full HA, separate deployment unit, zone redundancy |
| Malformed response behaviour | | |
| Authorization header present and validatable? | | |

### Consequences

- [ ] If the latency envelope is not achievable, switch to the degraded mode in `docs/identity/02-auth-flows.md` §2.6 — org binding from directory extension attributes.
- [ ] If a 500 blocks sign-in, raise the identity service's availability target and separate its release cadence from product code.

---

## R3 · Cross-organisation IdP reachability

| | |
| --- | --- |
| Run on | _date_ |
| Evidence | `spikes/evidence/r3_domain_routing/<run>/` |

| Question | Answer |
| --- | --- |
| Does the baseline sign-in page list every configured provider? | |
| Does `domain_hint` suppress other organisations' providers? | |
| Is another organisation's provider reachable by crafting the URL? | |

### Consequences

- [ ] If cross-org is reachable, the "org from user record, never from IdP" rule is **load-bearing**, not defence in depth. Add a build-gating test that an unexpected IdP yields a token with no `cube_org_id`.
- [ ] Wire the IdP-mismatch detector to a P1 alert.
- [ ] Keep JIT provisioning off by default with no exception that derives org from anything but a verified email domain.

---

## R4 · Directory enumeration

| | |
| --- | --- |
| Run on | _date_ |
| Evidence | `spikes/evidence/r4_directory_enumeration/<run>/` |

| Probe | Blocked? |
| --- | --- |
| List all users | |
| Filter users by domain | |
| List groups | |
| List identity providers | |
| List applications | |

### Consequences

- [ ] Any leak is a **P1** and blocks Phase 2.
- [ ] Promote these probes to a CI regression test against non-prod — tenant defaults change, so this answer has a shelf life.
- [ ] This is the evidence for the enumeration question in customer security questionnaires. Keep it current.

---

## Gate to Phase 2

Phase 2 scope is not locked until **R1 and R2 have answers**. R3 and R4 can land in
parallel with Phase 1 build, but a leak in R4 stops everything.
