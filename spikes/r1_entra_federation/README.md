# R1 — Entra-to-Entra federation

**The highest-leverage unknown in the entire design, and it is commercial rather than
technical.** Large pharma and medical device organisations are disproportionately on
Microsoft Entra. If federating their tenant into ours is blocked and we discover that after
signing a contract, we have promised an integration we cannot deliver.

## Setup (about an hour, mostly waiting)

1. **Create a disposable External ID external tenant.** Microsoft Entra admin centre →
   Identity → Overview → Manage tenants → Create → **External**. Not a workforce tenant —
   the federation surfaces differ completely and a workforce result would tell you nothing.

2. **Create a disposable workforce tenant** to stand in for a customer like Acme.

3. **Verify a custom domain in the workforce tenant.** This is the case most likely to be
   blocked, so it is the case you most need to test. A domain you already control with a
   DNS TXT record you can set.

4. **App registration in the CIAM tenant** for the spike scripts, with application
   permissions `IdentityProvider.ReadWrite.All`, `Application.Read.All`,
   `Domain.ReadWrite.All`. Grant admin consent. Note the client id and secret.

5. **App registration in the partner tenant** to act as the OIDC client for attempt A1.
   Note its client id and secret — these go into `matrix.json` where the
   `REPLACE_WITH_...` placeholders are.

6. Fill in `spikes/spike-config.json`, export the secrets, and run.

## Running

```bash
export SPIKE_CIAM_SECRET='...'
export SPIKE_PARTNER_SECRET='...'

python -m spikes.r1_entra_federation.run_matrix
python -m spikes.r1_entra_federation.test_trusted_issuer
```

## Reading the output

**A blocked attempt is a successful spike.** The finding is the exact error, not whether it
worked. What you need out of this is a decision table, not a yes/no.

Three things make a result trustworthy:

- **A0, the control, must pass.** If creating a generic OIDC provider fails, the tenant or
  permissions are wrong and nothing else is interpretable. The script stops if it fails.
- **Placeholders are skipped, not sent.** An attempt whose body still says `REPLACE_WITH_…`
  is skipped, because a 400 caused by a placeholder reaching Graph looks exactly like a
  product limitation in the transcript.
- **A3 vs A4 isolates the cause.** If SAML fails for a *verified* domain and succeeds for an
  *unverified* one, the blocker is specifically domain verification in the partner tenant.
  That is a precise, quotable finding. "SAML didn't work" is not.

Every attempt records Graph's `request-id`. **Quote it when escalating to Microsoft** — it
is the only thing that lets them find your specific request in their logs.

## When the matrix is wrong

It will be. The request bodies are what the documentation implies; several are hypotheses.
**Edit `matrix.json` and re-run.** Add variants when an error suggests one — a different
`responseType`, the beta endpoint, an issuer without the `/v2.0` suffix. Each attempt lists
`on_failure_try` hints for exactly this.

A spike that only confirms what you already believed was not worth running.

## Whatever the outcome

Run `test_trusted_issuer.py` too. If federation is blocked, that path *is* the path. If
federation works, it may still be preferable for large Entra customers, because those users
authenticate against their own tenant and are therefore not our MAU. That arithmetic
belongs in the decision, not as an afterthought.
