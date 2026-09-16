"""
R2 — read back the endpoint's evidence and answer the three questions that matter.

    python -m spikes.r2_token_issuance.analyze            # newest run
    python -m spikes.r2_token_issuance.analyze --run 20260921T101500Z

Separate from the endpoint so you can analyse a run after the fact, including one captured
by someone else on a different machine.
"""

from __future__ import annotations

import argparse
import json
import statistics
import sys

from ..common.spikelib import EVIDENCE_ROOT, banner, fail

SPIKE_DIR = EVIDENCE_ROOT / "r2_token_issuance"


def load_run(run: str | None) -> tuple[str, list[dict]]:
    if not SPIKE_DIR.exists():
        fail(f"No evidence at {SPIKE_DIR}. Run the endpoint and drive at least one sign-in first.")
    runs = sorted(p for p in SPIKE_DIR.iterdir() if p.is_dir())
    if not runs:
        fail(f"No runs under {SPIKE_DIR}.")
    chosen = next((p for p in runs if p.name == run), None) if run else runs[-1]
    if chosen is None:
        fail(f"No run named '{run}'. Available: {', '.join(p.name for p in runs)}")
    events = [json.loads(line) for line in (chosen / "transcript.jsonl").read_text().splitlines() if line.strip()]
    return chosen.name, events


def main() -> int:
    parser = argparse.ArgumentParser(description="Analyse an R2 endpoint run")
    parser.add_argument("--run", help="Run directory name; defaults to the newest")
    args = parser.parse_args()

    name, events = load_run(args.run)
    invocations = [e for e in events if e.get("kind") == "invocation"]

    banner("R2 analysis", f"run {name} · {len(invocations)} invocation(s)")
    if not invocations:
        print("\n  No invocations recorded. Either Entra never called the endpoint, or the")
        print("  extension is not registered against the user flow. Check, in order:")
        print("    1. Is the endpoint publicly reachable over HTTPS?")
        print("    2. Is the custom authentication extension registered?")
        print("    3. Is it associated with the user flow the sign-in actually used?")
        return 1

    # -- Q1: latency budget -------------------------------------------------
    print("\nQ1. What is the real latency budget?")
    by_delay: dict[int, list[dict]] = {}
    for i in invocations:
        by_delay.setdefault(i.get("injected_delay_ms", 0), []).append(i)

    print(f"    {'injected delay':>15}  {'invocations':>11}")
    for delay in sorted(by_delay):
        print(f"    {delay:>13}ms  {len(by_delay[delay]):>11}")

    if len(by_delay) > 1:
        print()
        print("    Cross-reference against whether the resulting tokens carried cube_org_id.")
        print("    The largest delay that still enriched is the real budget — design to HALF of it,")
        print("    because production adds database latency and cold starts on top.")
    else:
        print()
        print("    Only one delay value tested. Sweep it — that is the point of the exercise:")
        print("      curl '<endpoint>/control?delay_ms=500'   then sign in")
        print("      curl '<endpoint>/control?delay_ms=1500'  then sign in")
        print("      curl '<endpoint>/control?delay_ms=3000'  then sign in")

    handled = [i["handled_ms"] for i in invocations]
    print()
    print(f"    Our own handling time: p50 {statistics.median(handled):.1f}ms, max {max(handled):.1f}ms")
    print("    (a trivial handler — production adds the org lookup, so budget accordingly)")

    # -- Q2: refresh --------------------------------------------------------
    print("\nQ2. Does it fire on refresh-driven issuance?")
    correlations = [i.get("correlation_id", "") for i in invocations if i.get("correlation_id")]
    distinct = len(set(correlations))
    print(f"    {len(correlations)} invocation(s) carrying {distinct} distinct correlation id(s)")
    if len(correlations) > distinct:
        print("    Repeated correlation ids — Entra invoked the extension more than once for the")
        print("    same authentication. Determine whether that is a RETRY or a REFRESH: a retry")
        print("    means our handler must be idempotent; a refresh means suspension propagates on")
        print("    every renewal, which is a materially stronger fail-closed story.")
    else:
        print("    Every invocation had a distinct correlation id.")
        print("    Drive a refresh explicitly and re-check — if the extension does NOT fire on")
        print("    refresh, a suspended org keeps working until the refresh token expires, and the")
        print("    APIM deny-list becomes the only fast revocation path.")

    # -- Q3: failure behaviour ---------------------------------------------
    print("\nQ3. What happens when we fail?")
    modes = {}
    for i in invocations:
        modes.setdefault(i.get("fail_mode") or "ok", 0)
        modes[i.get("fail_mode") or "ok"] += 1
    for mode, count in sorted(modes.items()):
        print(f"    {mode:<12} {count:>4} invocation(s)")

    untested = {"500", "timeout", "malformed", "empty"} - set(modes)
    if untested:
        print()
        print(f"    NOT YET TESTED: {', '.join(sorted(untested))}")
        print("    The critical one is 500. It decides an architecture:")
        print("      · if sign-in BLOCKS  → this endpoint is a hard availability dependency for")
        print("                             every customer, and needs full HA treatment")
        print("      · if sign-in DEGRADES → our fail-closed design holds; the token lacks")
        print("                             cube_org_id and everything downstream rejects it")

    # -- auth ---------------------------------------------------------------
    print("\nSecurity check")
    unauth = [i for i in invocations if not i.get("has_auth_header")]
    if unauth:
        print(f"    ⚠ {len(unauth)} invocation(s) arrived with NO Authorization header.")
        print("      Entra should authenticate to this endpoint. Without it, anyone who learns the")
        print("      URL can assert an organisation for any user.")
    else:
        schemes = {i.get("auth_scheme") for i in invocations}
        print(f"    All invocations carried an Authorization header ({', '.join(str(s) for s in schemes)}).")
        print("    Production must VALIDATE it: issuer = our tenant, audience = the extension app id.")

    print("\n" + "=" * 72)
    print("  Fill in the R2 row of spikes/RESULTS.md.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
