"""
R1 — Entra-to-Entra federation matrix.

Runs each attempt in matrix.json against the disposable External ID tenant, records the
exact status / error code / message / Graph request-id, and cleans up anything it created.

    python -m spikes.r1_entra_federation.run_matrix
    python -m spikes.r1_entra_federation.run_matrix --only A1-oidc-partner-entra
    python -m spikes.r1_entra_federation.run_matrix --no-cleanup   # leave objects for portal inspection

**Read the output as data, not as pass/fail.** A 400 with
"domain is already verified in another tenant" is a *successful spike* — it answers the
question. The failure mode to worry about is an attempt that errors for a boring reason
(bad client id, wrong API version) and gets mistaken for a product limitation. That is why
A0 is a control: if the control fails, nothing else means anything.
"""

from __future__ import annotations

import argparse
import json
import pathlib
import sys

from ..common.spikelib import (
    Evidence,
    GraphClient,
    GraphResult,
    SpikeConfigError,
    SpikeSafetyError,
    banner,
    fail,
    get_tenant,
    load_config,
)

MATRIX_PATH = pathlib.Path(__file__).resolve().parent / "matrix.json"

# Attempts whose body still carries an example placeholder are skipped rather than run,
# because a 400 caused by "REPLACE_WITH..." reaching Graph looks exactly like a product
# limitation in the transcript and would poison the finding.
PLACEHOLDER_MARKERS = ("REPLACE_WITH", "spike-control-client-id", "spike-control-secret")


def substitute(obj, values: dict[str, str]):
    """Recursively fill {placeholder} tokens in strings."""
    if isinstance(obj, str):
        for key, val in values.items():
            obj = obj.replace("{" + key + "}", val)
        return obj
    if isinstance(obj, dict):
        return {k: substitute(v, values) for k, v in obj.items()}
    if isinstance(obj, list):
        return [substitute(v, values) for v in obj]
    return obj


def has_placeholder(body) -> str | None:
    blob = json.dumps(body) if body is not None else ""
    for marker in PLACEHOLDER_MARKERS:
        if marker in blob:
            return marker
    return None


def created_object_id(result: GraphResult) -> str | None:
    if result.ok and isinstance(result.body, dict):
        return result.body.get("id")
    return None


def main() -> int:
    parser = argparse.ArgumentParser(description="R1 Entra-to-Entra federation matrix")
    parser.add_argument("--only", help="Run a single attempt by id")
    parser.add_argument("--no-cleanup", action="store_true", help="Leave created objects in place")
    args = parser.parse_args()

    try:
        config = load_config()
        ciam = get_tenant(config, "spike_ciam")
        partner = get_tenant(config, "spike_partner")
    except (SpikeConfigError, SpikeSafetyError) as exc:
        fail(str(exc))
        return 1

    if ciam.kind != "external":
        fail(
            f"spike_ciam is configured as kind='{ciam.kind}'. R1 must run against an External ID "
            "external tenant — a workforce tenant has different federation surfaces entirely and "
            "any result would be meaningless."
        )

    matrix = json.loads(MATRIX_PATH.read_text())
    attempts = matrix["attempts"]
    if args.only:
        attempts = [a for a in attempts if a["id"] == args.only]
        if not attempts:
            fail(f"No attempt with id '{args.only}'")

    values = {
        "partner_tenant_id": partner.tenant_id,
        "partner_domain": partner.domain,
        "partner_custom_domain": partner.verified_custom_domain or "UNSET_VERIFIED_DOMAIN",
        "ciam_tenant_id": ciam.tenant_id,
    }

    banner("R1 — Entra-to-Entra federation", f"target: {ciam.name} ({ciam.tenant_id})")
    print(f"  partner tenant : {partner.tenant_id}")
    print(f"  verified domain: {values['partner_custom_domain']}")
    print(f"  attempts       : {len(attempts)}")

    rows: list[dict] = []

    with Evidence("r1_entra_federation") as ev:
        ev.note("spike_context", ciam_tenant=ciam.tenant_id, partner_tenant=partner.tenant_id)
        client = GraphClient(ciam, ev)

        for attempt in attempts:
            aid = attempt["id"]
            body = substitute(attempt.get("body"), values)

            print(f"\n── {aid} " + "─" * max(0, 60 - len(aid)))
            print(f"   Q: {attempt['question']}")

            marker = has_placeholder(body)
            if marker:
                print(f"   SKIPPED — body still contains placeholder '{marker}'.")
                print("            Fill it in in matrix.json, or this attempt's failure would be")
                print("            indistinguishable from a real product limitation.")
                ev.note("attempt_skipped", attempt=aid, reason=f"placeholder {marker}")
                rows.append({"id": aid, "outcome": "SKIPPED", "detail": f"placeholder {marker}", "request_id": ""})
                continue

            result = client.request(
                attempt["method"],
                attempt["path"],
                json_body=body,
                api_version=attempt.get("api_version", "v1.0"),
                label=aid,
            )

            print(f"   → {result.one_line()}")
            if result.request_id:
                print(f"     request-id: {result.request_id}   (quote this to Microsoft support)")

            outcome = "SUCCESS" if result.ok else "BLOCKED"
            rows.append({
                "id": aid,
                "outcome": outcome,
                "status": result.status,
                "detail": result.one_line(),
                "request_id": result.request_id,
                "expected": attempt.get("expect", "unknown"),
            })

            if not result.ok and attempt.get("on_failure_try"):
                print("     Next things to try (then edit matrix.json and re-run):")
                for hint in attempt["on_failure_try"]:
                    print(f"       · {hint}")

            # -- cleanup ----------------------------------------------------
            obj_id = created_object_id(result)
            if obj_id and attempt.get("cleanup") and not args.no_cleanup:
                base = attempt["path"].rstrip("/")
                delete = client.delete(
                    f"{base}/{obj_id}",
                    api_version=attempt.get("api_version", "v1.0"),
                    label=f"{aid}-cleanup",
                )
                print(f"     cleanup: {delete.one_line()}")
                if not delete.ok:
                    print(f"     ⚠ MANUAL CLEANUP NEEDED — {base}/{obj_id}")

            if aid == "A0-control" and not result.ok:
                print("\n   ⛔ THE CONTROL FAILED.")
                print("      Every subsequent result is uninterpretable until this passes —")
                print("      you cannot distinguish 'the feature is blocked' from 'the spike is")
                print("      misconfigured'. Fix permissions/tenant setup and re-run.")
                ev.note("control_failed", detail=result.one_line())
                break

        summary = render_summary(rows, ciam.tenant_id, partner.tenant_id)
        path = ev.write_summary("R1 — Entra-to-Entra federation", summary)

    print("\n" + "=" * 72)
    print(summary)
    print(f"\nEvidence: {path.parent}")
    print("Fill in the R1 row of spikes/RESULTS.md from this.")
    return 0


def render_summary(rows: list[dict], ciam_tid: str, partner_tid: str) -> str:
    lines = [
        f"CIAM tenant `{ciam_tid}` · partner tenant `{partner_tid}`",
        "",
        "| Attempt | Outcome | Detail | Graph request-id |",
        "| --- | --- | --- | --- |",
    ]
    for r in rows:
        lines.append(
            f"| `{r['id']}` | **{r['outcome']}** | {r.get('detail','')} | `{r.get('request_id','') or '—'}` |"
        )

    blocked = [r for r in rows if r["outcome"] == "BLOCKED"]
    ok = [r for r in rows if r["outcome"] == "SUCCESS"]
    skipped = [r for r in rows if r["outcome"] == "SKIPPED"]

    lines += ["", "## Reading this", ""]
    if skipped:
        lines.append(
            f"- {len(skipped)} attempt(s) skipped for unfilled placeholders — **fill them in and "
            "re-run before drawing conclusions.**"
        )
    if any(r["id"] == "A0-control" and r["outcome"] != "SUCCESS" for r in rows):
        lines.append("- ⛔ **The control failed.** Nothing below it is interpretable.")
    else:
        lines.append(f"- {len(ok)} succeeded, {len(blocked)} blocked.")
        lines.append(
            "- A *blocked* result is a finding, not a failure. Record the exact error code in "
            "RESULTS.md — 'domain already verified in another tenant' and 'unsupported provider' "
            "lead to different customer conversations."
        )
    lines += [
        "",
        "## Next",
        "",
        "- If **A1 or A2 succeeded** → standard federation is viable for Entra customers. Record "
        "the caveats and move to R3.",
        "- If **both blocked** → the trusted-issuer path (ADR-0004) is not a fallback, it is the "
        "path. Run `test_trusted_issuer.py` to prove it end to end, then revisit the MAU model: "
        "those users are not our MAU, which may make this preferable regardless.",
        "- Either way, **no federation date is quotable to a customer until this table is filled in.**",
    ]
    return "\n".join(lines)


if __name__ == "__main__":
    sys.exit(main())
