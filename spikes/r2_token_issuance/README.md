# R2 — `OnTokenIssuanceStart` custom authentication extension

This mechanism carries the org claim, and it sits on the critical authentication path for
every customer. If it does not work, or is too slow, the claim architecture changes.

## The question that decides an architecture

> **Does a failure here block sign-in, or does Entra issue the token without our claims?**

- **Blocks** → this endpoint is a hard availability dependency for all 30 customers. It needs
  zone redundancy, a separate deployment unit, a separate release cadence, and a read
  replica for lookups.
- **Degrades** → our fail-closed design holds. The token lacks `cube_org_id`, APIM and every
  API reject it, and the endpoint is merely important.

Do not guess. `--fail-mode 500` plus one sign-in answers it.

## Setup

1. Run the endpoint and expose it over **HTTPS** — Entra will not call plain HTTP. A dev
   tunnel, ngrok, or a throwaway Container App all work.

   ```bash
   python -m spikes.r2_token_issuance.endpoint --port 8080
   ```

2. Register a **custom authentication extension** in the CIAM tenant pointing at
   `https://<public>/internal/token-issuance`, with the claims
   `cube_org_id`, `cube_org_slug`, `cube_idp`, `cube_ver`.

3. Associate it with the user flow your test app uses. *This is the step people miss* — an
   extension registered but not associated simply never fires, which looks identical to a
   broken endpoint.

4. Sign in, then decode the resulting token and check for `cube_org_id`.

## The delay sweep

The point of the exercise. Raise the injected delay until enrichment stops:

```bash
curl 'https://<public>/control?delay_ms=500'   # sign in, check the token
curl 'https://<public>/control?delay_ms=1500'  # sign in, check the token
curl 'https://<public>/control?delay_ms=3000'  # sign in, check the token
```

The largest delay that still produced an enriched token is the real budget. **Design to half
of it** — production adds the org lookup, connection acquisition and cold starts on top of a
handler that currently does nothing.

## Failure injection

```bash
curl 'https://<public>/control?fail_mode=500'        # the critical one
curl 'https://<public>/control?fail_mode=timeout'    # never responds
curl 'https://<public>/control?fail_mode=malformed'  # wrong response shape
curl 'https://<public>/control?fail_mode=empty'      # valid shape, no claims — our fail-closed path
curl 'https://<public>/control?fail_mode=none'       # back to normal
```

For each: does sign-in succeed? Does the token carry `cube_org_id`? Both answers matter.

## Refresh behaviour

Sign in, then force a token refresh, then check whether the extension fired again. If it does
**not** fire on refresh, a suspended organisation keeps working until its refresh token
expires — and the APIM deny-list becomes the only fast revocation path, which raises its
importance considerably.

## Analysis

```bash
python -m spikes.r2_token_issuance.analyze
```

Reads the evidence transcript and answers the three questions. Runs against a capture from
any machine, so whoever ran the sign-ins need not be whoever interprets them.

## Note on the language

This endpoint is Python because the spike needs to be deployable in minutes. The production
handler is .NET, in `CubeRM.Identity.Api`. What is being measured is **Entra's** behaviour,
so the handler's language is irrelevant — but the response contract this confirms is what
`TokenIssuanceContracts.cs` must match.
