# R4 — directory enumeration

Answers a question a pharma customer's security questionnaire **will** ask and which we
currently cannot answer with evidence: *can one customer's user enumerate another's?*

## Setup

1. Two users in the disposable CIAM tenant, in different notional organisations.
2. An app registration with **"Allow public client flows" enabled** (device code flow needs
   it) and delegated `User.Read`.
3. Fill in `spike-config.json`.

```bash
export SPIKE_CIAM_SECRET='...'
python -m spikes.r4_directory_enumeration.run_probes
# sign in as a user belonging to ORG A only, when prompted
```

## Why delegated, not app-only

Our provisioning service principal deliberately holds tenant-wide Graph permissions, so an
app-only token proves nothing about what a *user* can do. The threat model is a customer's
end user, so the probe must run as one. Hence device code and a real sign-in.

## The probes

Broadest first, because a broad success makes the narrow ones moot. The one to care about
most is **`list identity providers`** — that response is the complete roster of every
customer's federated IdP configuration, which leaks the customer list itself.

`read own profile` is the control. If it does not return 200 the token is wrong and no other
result means anything; the script says so rather than reporting false comfort.

## Severity

A demonstrated cross-customer enumeration is a **P1** and blocks Phase 2. It is a reportable
incident and a failed security review even if no tender data leaks: in Life Sciences the
customer list is commercially sensitive on its own — knowing which pharma companies run Cube
RM is valuable to a competitor independent of any document they hold.

## Shelf life

A clean result today is not a clean result forever. Tenant defaults change, and app
registrations accumulate scopes. **Promote these probes to a CI regression test against
non-prod** so the answer stays true, and re-run with a token carrying every scope the real
applications request — a narrow spike token under-reports.
