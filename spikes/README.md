# Phase 0 Spikes

Four spikes that gate the identity build. **R1 and R2 are the critical path and are
independent — run them in parallel from day one.** Nothing in Phase 2 is worth starting
before they have answers.

| Spike | Question | Blocks | Effort |
| --- | --- | --- | --- |
| [R1](r1_entra_federation/) | Can a customer's own Entra tenant be federated into our External ID tenant? | Phase 2 scope, every federation date we quote | 3–5 d |
| [R2](r2_token_issuance/) | Does `OnTokenIssuanceStart` work, fast enough, on every issuance path? | The claim architecture, identity-service availability design | 3–4 d |
| [R3](r3_domain_routing/) | Can a user reach another customer's identity provider? | Whether our HRD layer is the only cross-org defence or the second one | 2–3 d |
| [R4](r4_directory_enumeration/) | Can one customer's user enumerate another's? | A security-questionnaire answer we currently cannot give | 1–2 d |

## The stance these scripts take

> **Documentation is a hypothesis. The spike is the test.**

The request bodies in `r1_entra_federation/matrix.json` are *what the docs imply should
work*. Several of them probably will not — that is the finding, not a bug in the script.
Every attempt records the exact HTTP status, error code, message, and Graph `request-id`,
because the `request-id` is what you quote when escalating to Microsoft support.

When an attempt fails in an interesting way, **edit the matrix and re-run**. It is designed
to be modified. A spike that only confirms what you already believed was not worth running.

## Prerequisites

```bash
python3 -m venv .venv && source .venv/bin/activate
pip install -r spikes/requirements.txt

# R3 only
npm install playwright && npx playwright install chromium
```

You need **three disposable tenants**, none of them production:

| Role | What | Why |
| --- | --- | --- |
| `spike_ciam` | An External ID **external** tenant | Stands in for our shared customer tenant |
| `spike_partner` | A standard Entra ID **workforce** tenant | Stands in for a customer like Acme who runs their own Entra |
| `spike_partner_domain` | A custom domain **verified in `spike_partner`** | The verified-domain case is the one most likely to be blocked |

Copy `spikes/spike-config.example.json` to `spikes/spike-config.json` and fill it in.
`spike-config.json` is gitignored.

## Safety

Every script refuses to run unless the target tenant is listed in `spike_tenants` **and**
carries `"disposable": true`. There is no override flag. These scripts create and delete
directory objects; pointing one at production would be a live incident, so the guard is
structural rather than advisory.

## Run order

```bash
# Day 1 — start both blockers at once, different people
python -m spikes.r1_entra_federation.run_matrix          # ~40 min unattended
python -m spikes.r2_token_issuance.endpoint              # deploy, then drive sign-ins

# Day 2-3 — once R1 has told you which federation path survives
python -m spikes.r1_entra_federation.test_trusted_issuer
node spikes/r3_domain_routing/probe_idp_reachability.mjs
python -m spikes.r4_directory_enumeration.run_probes

# Any time
python -m spikes.r2_token_issuance.analyze
```

Each run appends to `spikes/evidence/<spike>/<timestamp>/` — a JSONL transcript of every
request and response, plus a markdown summary. **Keep the evidence directory.** It is what
turns "we think SAML federation is blocked" into "here is the request, the response, and
the Graph request-id, on this date."

## Recording the outcome

Every spike writes into one place: **[RESULTS.md](RESULTS.md)**. That file holds the
decision table that Phase 2 scope, the onboarding decision gate, and the sales conversation
all read from. A spike is not finished until its row there is filled in.
