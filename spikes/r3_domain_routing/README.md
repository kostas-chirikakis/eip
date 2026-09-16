# R3 — cross-organisation identity provider reachability

An isolation test wearing UX clothing. If the sign-in page lists configured identity
providers, then in our shared tenant that list is potentially every customer's IdP.

## Setup

1. Two identity providers in the disposable CIAM tenant, named `spike-org-a-*` and
   `spike-org-b-*`, **both associated with the same user flow**. That mirrors production,
   where one app registration serves every customer — configuring them against separate
   flows would make the test pass for the wrong reason.
2. One app registration with redirect URI `http://localhost:5173/callback`.
3. Fill in the `r3` block of `spike-config.json`.

```bash
npm install playwright && npx playwright install chromium
node spikes/r3_domain_routing/probe_idp_reachability.mjs --headed
```

## The four probes

| | Probe | Asks |
| --- | --- | --- |
| P1 | Baseline | What does the page offer with no hint at all? |
| P2 | `domain_hint` | Does hinting org A's domain suppress org B's provider? |
| P3 | `login_hint` | Does an email address work when `domain_hint` does not? |
| P4 | Direct navigation | Can org B's provider be reached by crafting the URL? |

**P4 is the one that matters.** Suppressing a button is presentation. If the underlying
authorize request still accepts an arbitrary provider, the control is cosmetic.

## Interpreting it

Finding that org B is reachable is **not** project-ending. Our design already resolves the
organisation from the pre-existing user record rather than from the IdP that asserted the
authentication, so a user who wanders into the wrong provider gets a token with no
`cube_org_id` — useless rather than mis-scoped.

What this decides is whether that rule is our **only** defence or our **second** one. If org
B is reachable it becomes load-bearing, and it needs a test that gates every build plus the
IdP-mismatch detector wired to a P1 alert.

Screenshots of every probe land in the evidence directory. Keep them — "here is the sign-in
page listing another customer's IdP" is more persuasive in a design review than a sentence.
