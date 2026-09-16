"""
R1 fallback — prove the trusted-issuer path (ADR-0004) works end to end.

Run this **whether or not federation worked**. If federation is blocked, this is the path.
If federation works, this is still worth proving, because the MAU arithmetic may make it
preferable for large Entra customers anyway: their users authenticate against their own
tenant and are therefore not our MAU.

    python -m spikes.r1_entra_federation.test_trusted_issuer

What it checks, in order — each step is a thing that can independently break:

  1. The partner tenant's OIDC discovery document is reachable and well-formed.
  2. Its JWKS is reachable and contains usable signing keys.
  3. A token minted by the partner tenant validates against that JWKS.
  4. The issuer in the token matches the discovery document's declared issuer
     (the classic mismatch: ``sts.windows.net/{tid}/`` vs ``login.microsoftonline.com/{tid}/v2.0``).
  5. The token carries a ``tid`` we can map one-to-one to a Cube RM organisation.

Step 4 is the one that bites. A v1.0 token and a v2.0 token from the same tenant declare
different issuers, and an issuer allowlist built from the wrong one rejects every token with
an error that looks like a key problem.
"""

from __future__ import annotations

import sys

import jwt
import requests
from jwt import PyJWKClient

from ..common.spikelib import (
    Evidence,
    SpikeConfigError,
    SpikeSafetyError,
    banner,
    fail,
    get_tenant,
    load_config,
)


def check(label: str, ok: bool, detail: str = "") -> bool:
    mark = "PASS" if ok else "FAIL"
    print(f"   [{mark}] {label}")
    if detail:
        print(f"          {detail}")
    return ok


def main() -> int:
    try:
        config = load_config()
        partner = get_tenant(config, "spike_partner")
    except (SpikeConfigError, SpikeSafetyError) as exc:
        fail(str(exc))
        return 1

    banner("R1 fallback — trusted issuer", f"partner tenant {partner.tenant_id}")

    results: list[tuple[str, bool]] = []

    with Evidence("r1_trusted_issuer") as ev:
        ev.note("context", partner_tenant=partner.tenant_id)

        # -- 1. discovery ---------------------------------------------------
        print("\n1. OIDC discovery")
        disco_url = (
            f"https://login.microsoftonline.com/{partner.tenant_id}/v2.0/.well-known/openid-configuration"
        )
        try:
            disco = requests.get(disco_url, timeout=30)
            disco.raise_for_status()
            disco_doc = disco.json()
        except Exception as exc:  # noqa: BLE001 — spike wants the raw reason
            ev.note("discovery_failed", url=disco_url, error=str(exc))
            check("discovery document reachable", False, str(exc))
            return 1

        declared_issuer = disco_doc.get("issuer", "")
        jwks_uri = disco_doc.get("jwks_uri", "")
        ev.note("discovery", url=disco_url, issuer=declared_issuer, jwks_uri=jwks_uri)
        results.append(("discovery reachable", check("discovery document reachable", True, disco_url)))
        results.append(("issuer declared", check("issuer declared", bool(declared_issuer), declared_issuer)))

        # -- 2. jwks --------------------------------------------------------
        print("\n2. Signing keys")
        try:
            jwks = requests.get(jwks_uri, timeout=30).json()
            key_count = len(jwks.get("keys", []))
        except Exception as exc:  # noqa: BLE001
            ev.note("jwks_failed", url=jwks_uri, error=str(exc))
            check("JWKS reachable", False, str(exc))
            return 1
        ev.note("jwks", url=jwks_uri, key_count=key_count)
        results.append(("jwks reachable", check("JWKS reachable", key_count > 0, f"{key_count} key(s)")))

        # -- 3. mint a token ------------------------------------------------
        print("\n3. Token from the partner tenant")
        token_url = f"https://login.microsoftonline.com/{partner.tenant_id}/oauth2/v2.0/token"
        token_resp = requests.post(
            token_url,
            data={
                "client_id": partner.client_id,
                "client_secret": partner.client_secret,
                "scope": "https://graph.microsoft.com/.default",
                "grant_type": "client_credentials",
            },
            timeout=30,
        )
        if token_resp.status_code != 200:
            ev.note("token_failed", status=token_resp.status_code, body=token_resp.text[:2000])
            check("token acquired", False, f"{token_resp.status_code} {token_resp.text[:200]}")
            return 1

        token = token_resp.json()["access_token"]
        unverified = jwt.decode(token, options={"verify_signature": False})
        token_issuer = unverified.get("iss", "")
        token_tid = unverified.get("tid", "")
        token_aud = unverified.get("aud", "")
        ev.note("token_claims", iss=token_issuer, tid=token_tid, aud=token_aud)
        results.append(("token acquired", check("token acquired", True, f"aud={token_aud}")))

        # -- 4. issuer agreement (the one that bites) ------------------------
        print("\n4. Issuer agreement")
        issuers_match = token_issuer.rstrip("/") == declared_issuer.rstrip("/")
        detail = f"token: {token_issuer}\n          discovery: {declared_issuer}"
        results.append(("issuer matches discovery", check("token issuer matches discovery issuer", issuers_match, detail)))
        if not issuers_match:
            print()
            print("      ⚠ This is the classic multi-issuer trap. A v1.0 token declares")
            print("        https://sts.windows.net/{tid}/ while a v2.0 token declares")
            print("        https://login.microsoftonline.com/{tid}/v2.0 — same tenant, different string.")
            print("        identity.trusted_issuers must store the issuer the TOKENS actually carry,")
            print("        and the client app must request the matching token version.")
            ev.note("issuer_mismatch", token_issuer=token_issuer, discovery_issuer=declared_issuer)

        # -- 5. signature validation ----------------------------------------
        print("\n5. Signature validation against the partner's JWKS")
        try:
            signing_key = PyJWKClient(jwks_uri).get_signing_key_from_jwt(token)
            jwt.decode(
                token,
                signing_key.key,
                algorithms=["RS256"],
                audience=token_aud,
                issuer=token_issuer,
                options={"verify_exp": True},
            )
            results.append(("signature validates", check("signature validates", True)))
        except Exception as exc:  # noqa: BLE001
            ev.note("validation_failed", error=str(exc))
            results.append(("signature validates", check("signature validates", False, str(exc))))

        # -- 6. org mapping -------------------------------------------------
        print("\n6. Organisation mapping")
        maps = bool(token_tid) and token_tid == partner.tenant_id
        results.append((
            "tid maps to one org",
            check(
                "tid present and matches the configured partner tenant",
                maps,
                f"tid={token_tid}",
            ),
        ))
        print()
        print("      In production this is a UNIQUE constraint on identity.trusted_issuers:")
        print("      one issuer serves exactly one organisation. That constraint — not a")
        print("      claims-mapping decision — is what makes this path as isolated as federation.")

        passed = sum(1 for _, ok in results if ok)
        total = len(results)
        summary = render_summary(results, partner.tenant_id, token_issuer, declared_issuer)
        path = ev.write_summary("R1 fallback — trusted issuer", summary)

    print("\n" + "=" * 72)
    print(f"  {passed}/{total} checks passed")
    print(f"  Evidence: {path.parent}")
    if passed == total:
        print("\n  The trusted-issuer path is viable. If R1's matrix showed federation blocked,")
        print("  this is your path for Entra customers — and it costs nothing in MAU.")
    else:
        print("\n  Resolve the failures above before relying on this as the R1 fallback.")
    return 0 if passed == total else 1


def render_summary(results, partner_tid: str, token_issuer: str, declared_issuer: str) -> str:
    lines = [
        f"Partner tenant `{partner_tid}`",
        "",
        "| Check | Result |",
        "| --- | --- |",
    ]
    for label, ok in results:
        lines.append(f"| {label} | {'PASS' if ok else '**FAIL**'} |")
    lines += [
        "",
        "## Issuer strings observed",
        "",
        f"- Token `iss`: `{token_issuer}`",
        f"- Discovery `issuer`: `{declared_issuer}`",
        "",
        "Whichever string the tokens actually carry is what goes in `identity.trusted_issuers`.",
    ]
    return "\n".join(lines)


if __name__ == "__main__":
    sys.exit(main())
