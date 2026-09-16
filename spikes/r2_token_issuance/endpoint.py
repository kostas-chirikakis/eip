"""
R2 — instrumented OnTokenIssuanceStart handler.

A deliberately minimal stand-in for CubeRM.Identity.Api's claims-enrichment endpoint,
built to *measure Entra's behaviour* rather than to be productionised. Python because the
spike needs to be deployable in minutes; the production handler is .NET.

    pip install -r spikes/requirements.txt
    python -m spikes.r2_token_issuance.endpoint --port 8080

Then expose it over HTTPS (Entra will not call plain HTTP) — a dev tunnel, ngrok, or a
throwaway Container App — and register it as a custom authentication extension pointing at
`https://<public>/internal/token-issuance`.

## What it is built to answer

| Question | How |
| --- | --- |
| What is the real latency budget? | `--delay-sweep` walks the injected delay upward until Entra gives up. The last delay that still enriched the token is the budget. |
| Does it fire on refresh? | Every invocation is logged with its correlation id. Sign in once, force a refresh, count invocations. |
| What happens when we fail? | `--fail-mode` injects 500 / timeout / malformed / empty-claims and records whether sign-in still succeeds and what the resulting token contains. |
| What is in the payload? | Every request body is recorded verbatim. The contract in `TokenIssuanceContracts.cs` is a hypothesis until this confirms it. |

## The question that matters most

**Does a failure here block sign-in, or does Entra issue the token without our claims?**

Those are completely different architectures. If it blocks, this endpoint is a hard
availability dependency for every customer and needs the full HA treatment. If it degrades,
our fail-closed design holds — the token lacks `cube_org_id` and every downstream rejects
it — and the endpoint is merely important rather than critical.

Do not guess. `--fail-mode 500` answers it in one sign-in.
"""

from __future__ import annotations

import argparse
import json
import statistics
import sys
import threading
import time
from datetime import datetime, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

from ..common.spikelib import Evidence, banner

# Response shapes. Documented contract — confirm against a captured live request.
RESPONSE_ODATA = "microsoft.graph.onTokenIssuanceStartResponseData"
PROVIDE_CLAIMS_ODATA = "microsoft.graph.tokenIssuanceStart.provideClaimsForToken"

STATE = {
    "delay_ms": 0,
    "fail_mode": None,      # None | "500" | "timeout" | "malformed" | "empty"
    "invocations": [],
    "evidence": None,
    "lock": threading.Lock(),
}


def claims_response(oid: str) -> dict:
    """The success shape: a deterministic fake org binding, so the token is checkable."""
    return {
        "data": {
            "@odata.type": RESPONSE_ODATA,
            "actions": [
                {
                    "@odata.type": PROVIDE_CLAIMS_ODATA,
                    "claims": {
                        "cube_org_id": "0192f4c1-0000-7000-8000-00000000a1a1",
                        "cube_org_slug": "spike-org-a",
                        "cube_idp": "local",
                        "cube_ver": "1",
                        # Echoed so a decoded token can be tied to a specific invocation.
                        "cube_spike_oid": oid[:36],
                    },
                }
            ],
        }
    }


def no_claims_response() -> dict:
    """The fail-closed shape: token issued, but without cube_org_id, so it is unusable."""
    return {"data": {"@odata.type": RESPONSE_ODATA, "actions": []}}


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *args):  # silence default stderr spam
        pass

    def do_POST(self):  # noqa: N802 — BaseHTTPRequestHandler API
        received = time.perf_counter()
        received_utc = datetime.now(timezone.utc).isoformat()

        length = int(self.headers.get("Content-Length", 0))
        raw = self.rfile.read(length) if length else b""
        try:
            payload = json.loads(raw)
        except ValueError:
            payload = {"_unparseable": raw[:2000].decode("utf-8", "replace")}

        auth_header = self.headers.get("Authorization", "")
        data = payload.get("data", {}) if isinstance(payload, dict) else {}
        ctx = data.get("authenticationContext", {}) or {}
        user = ctx.get("user") or {}
        correlation = ctx.get("correlationId", "")
        oid = user.get("id", "")

        with STATE["lock"]:
            delay_ms = STATE["delay_ms"]
            fail_mode = STATE["fail_mode"]

        # --- injected behaviour -------------------------------------------
        if delay_ms:
            time.sleep(delay_ms / 1000.0)

        if fail_mode == "timeout":
            # Hold the connection open well past any plausible budget and never answer.
            time.sleep(120)
            return

        if fail_mode == "500":
            body, status = b'{"error":"spike injected failure"}', 500
        elif fail_mode == "malformed":
            body, status = b'{"data":{"@odata.type":"nonsense","actions":"not-an-array"}}', 200
        elif fail_mode == "empty":
            body, status = json.dumps(no_claims_response()).encode(), 200
        else:
            body, status = json.dumps(claims_response(oid)).encode(), 200

        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

        handled_ms = round((time.perf_counter() - received) * 1000, 1)
        record = {
            "utc": received_utc,
            "correlation_id": correlation,
            "user_oid": oid,
            "user_mail": user.get("mail") or user.get("userPrincipalName"),
            "tenant_id": data.get("tenantId"),
            "odata_type": data.get("@odata.type"),
            "has_auth_header": bool(auth_header),
            "auth_scheme": auth_header.split(" ")[0] if auth_header else None,
            "injected_delay_ms": delay_ms,
            "fail_mode": fail_mode,
            "responded_status": status,
            "handled_ms": handled_ms,
            "payload": payload,
        }

        with STATE["lock"]:
            STATE["invocations"].append(record)
            n = len(STATE["invocations"])
            if STATE["evidence"]:
                STATE["evidence"].note("invocation", **record)

        print(
            f"  #{n:<3} {received_utc}  corr={correlation[:8] or '—':8}  "
            f"oid={oid[:8] or '—':8}  delay={delay_ms:>5}ms  "
            f"mode={fail_mode or 'ok':9}  →{status}"
        )
        if not auth_header:
            print("       ⚠ NO Authorization header. Entra should authenticate to this endpoint;")
            print("         an unauthenticated caller could assert an org for any user.")

    def do_GET(self):  # noqa: N802
        """Control surface, so you can change behaviour without redeploying."""
        from urllib.parse import parse_qs, urlparse

        parsed = urlparse(self.path)
        query = parse_qs(parsed.query)

        if parsed.path == "/control":
            with STATE["lock"]:
                if "delay_ms" in query:
                    STATE["delay_ms"] = int(query["delay_ms"][0])
                if "fail_mode" in query:
                    mode = query["fail_mode"][0]
                    STATE["fail_mode"] = None if mode in ("", "none", "ok") else mode
                state = {"delay_ms": STATE["delay_ms"], "fail_mode": STATE["fail_mode"],
                         "invocations": len(STATE["invocations"])}
            print(f"  control → {state}")
            self._json(state)
            return

        if parsed.path == "/stats":
            self._json(summarise())
            return

        self._json({"ok": True, "endpoint": "/internal/token-issuance"})

    def _json(self, obj):
        body = json.dumps(obj, indent=2, default=str).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)


def summarise() -> dict:
    with STATE["lock"]:
        invocations = list(STATE["invocations"])

    if not invocations:
        return {"invocations": 0}

    handled = [i["handled_ms"] for i in invocations]
    correlations = [i["correlation_id"] for i in invocations if i["correlation_id"]]
    repeated = len(correlations) - len(set(correlations))

    successes_by_delay: dict[int, int] = {}
    for i in invocations:
        successes_by_delay.setdefault(i["injected_delay_ms"], 0)
        successes_by_delay[i["injected_delay_ms"]] += 1

    return {
        "invocations": len(invocations),
        "handled_ms": {
            "p50": round(statistics.median(handled), 1),
            "p95": round(sorted(handled)[int(len(handled) * 0.95)], 1) if len(handled) > 1 else handled[0],
            "max": max(handled),
        },
        "distinct_correlation_ids": len(set(correlations)),
        "repeated_correlation_ids": repeated,
        "_repeated_means": (
            "Entra invoked the extension more than once for the same authentication — "
            "check whether that is a retry or a refresh-driven issuance."
        ),
        "invocations_by_injected_delay": successes_by_delay,
        "auth_header_present_on_all": all(i["has_auth_header"] for i in invocations),
    }


def main() -> int:
    parser = argparse.ArgumentParser(description="R2 token-issuance spike endpoint")
    parser.add_argument("--port", type=int, default=8080)
    parser.add_argument("--delay-ms", type=int, default=0, help="Injected delay, to find the timeout budget")
    parser.add_argument(
        "--fail-mode",
        choices=["500", "timeout", "malformed", "empty"],
        help="Inject a failure and observe whether sign-in still succeeds",
    )
    args = parser.parse_args()

    STATE["delay_ms"] = args.delay_ms
    STATE["fail_mode"] = args.fail_mode

    banner("R2 — OnTokenIssuanceStart", f"listening on :{args.port}")
    print("  POST /internal/token-issuance     the extension endpoint")
    print("  GET  /control?delay_ms=&fail_mode= change behaviour without redeploying")
    print("  GET  /stats                        current measurements")
    print()
    print("  Entra requires HTTPS. Put a tunnel in front of this before registering it.")
    print()
    print("  The delay sweep: raise delay_ms until the token stops carrying cube_org_id.")
    print("  The last delay that still enriched is your real budget — design to half of it.")
    print()

    with Evidence("r2_token_issuance") as ev:
        STATE["evidence"] = ev
        ev.note("endpoint_started", port=args.port, delay_ms=args.delay_ms, fail_mode=args.fail_mode)
        server = ThreadingHTTPServer(("0.0.0.0", args.port), Handler)
        try:
            server.serve_forever()
        except KeyboardInterrupt:
            print("\n\nStopping.")
        finally:
            stats = summarise()
            ev.note("final_stats", **stats)
            print(json.dumps(stats, indent=2, default=str))
            ev.write_summary(
                "R2 — OnTokenIssuanceStart",
                "```json\n" + json.dumps(stats, indent=2, default=str) + "\n```\n\n"
                "Fill in the R2 row of `spikes/RESULTS.md`, especially:\n\n"
                "- the delay at which enrichment stopped (the real budget)\n"
                "- whether a 500 blocked sign-in or degraded it\n"
                "- whether the extension fired on refresh\n",
            )
    return 0


if __name__ == "__main__":
    sys.exit(main())
