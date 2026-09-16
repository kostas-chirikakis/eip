"""
Verify the spike harness safety guard actually refuses.

The guard is the only thing standing between a spike script and a production tenant, and
it has no override flag by design. That makes it exactly the kind of control that must be
tested rather than assumed, so this runs in CI on every change.
"""
import json, os, pathlib, shutil, sys
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent.parent))

from spikes.common import spikelib
from spikes.common.spikelib import SpikeSafetyError, SpikeConfigError, get_tenant, Evidence

REAL_TID = "7c3f9a12-4d6b-4e21-9f88-2b5c1e0a7d43"
os.environ["TEST_SECRET"] = "not-a-real-secret"

def cfg(**over):
    t = {"tenant_id": REAL_TID, "domain": "x.onmicrosoft.com", "kind": "external",
         "authority": "https://x.ciamlogin.com", "disposable": True,
         "client_id": "aaaaaaaa-1111-2222-3333-444444444444",
         "client_secret_env": "TEST_SECRET"}
    t.update(over)
    return {"spike_tenants": {"t": t}}

passed = failed = 0
def case(name, fn, expect):
    global passed, failed
    try:
        fn(); got = None
    except Exception as e:
        got = type(e)
    ok = (got is expect) if expect else (got is None)
    print(f"  [{'PASS' if ok else 'FAIL'}] {name}" + ("" if ok else f"  (got {got}, expected {expect})"))
    passed, failed = (passed + ok, failed + (not ok))

print("\nSafety guard")
case("refuses a tenant not marked disposable",
     lambda: get_tenant(cfg(disposable=False), "t"), SpikeSafetyError)
case("refuses when disposable key is absent",
     lambda: get_tenant({"spike_tenants": {"t": {"tenant_id": REAL_TID, "domain": "d",
        "client_id": "c", "client_secret_env": "TEST_SECRET"}}}, "t"), SpikeSafetyError)
case("refuses a placeholder tenant id",
     lambda: get_tenant(cfg(tenant_id="00000000-0000-0000-0000-000000000000"), "t"), SpikeConfigError)
case("refuses when the secret env var is unset",
     lambda: get_tenant(cfg(client_secret_env="DEFINITELY_UNSET_VAR"), "t"), SpikeConfigError)
case("refuses an unknown tenant name",
     lambda: get_tenant(cfg(), "nope"), SpikeConfigError)
case("accepts a properly configured disposable tenant",
     lambda: get_tenant(cfg(), "t"), None)

print("\nMatrix helpers")
from spikes.r1_entra_federation.run_matrix import substitute, has_placeholder
sub = substitute({"issuer": "https://login.microsoftonline.com/{partner_tenant_id}/v2.0",
                  "nested": [{"id": "{partner_custom_domain}"}]},
                 {"partner_tenant_id": REAL_TID, "partner_custom_domain": "acme.example"})
case("substitutes placeholders recursively",
     lambda: (_ for _ in ()).throw(AssertionError) if sub["issuer"] != f"https://login.microsoftonline.com/{REAL_TID}/v2.0"
             or sub["nested"][0]["id"] != "acme.example" else None, None)
case("detects an unfilled REPLACE_WITH placeholder",
     lambda: (_ for _ in ()).throw(AssertionError) if has_placeholder({"clientId": "REPLACE_WITH_APP"}) is None else None, None)
case("passes a fully substituted body",
     lambda: (_ for _ in ()).throw(AssertionError) if has_placeholder(sub) is not None else None, None)

print("\nEvidence harness")
import tempfile
tmp = pathlib.Path(tempfile.mkdtemp()) / "ev"
shutil.rmtree(tmp, ignore_errors=True)
spikelib.EVIDENCE_ROOT = tmp
with Evidence("selftest") as ev:
    ev.note("graph_call", label="A1", status=400, request_id="abc-123")
    ev.note("graph_call", label="A2", status=201, request_id="def-456")
    ev.write_summary("Self test", "| a | b |\n| --- | --- |")
runs = list((tmp / "selftest").iterdir())
lines = (runs[0] / "transcript.jsonl").read_text().strip().splitlines()
case("creates a timestamped run directory", lambda: None if len(runs) == 1 else (_ for _ in ()).throw(AssertionError), None)
case("writes one JSONL event per note (start + 2 calls + finish)",
     lambda: None if len(lines) == 4 else (_ for _ in ()).throw(AssertionError(f"{len(lines)} lines")), None)
case("records the graph request-id",
     lambda: None if json.loads(lines[1])["request_id"] == "abc-123" else (_ for _ in ()).throw(AssertionError), None)
case("writes SUMMARY.md", lambda: None if (runs[0] / "SUMMARY.md").exists() else (_ for _ in ()).throw(AssertionError), None)

print(f"\n{passed} passed, {failed} failed")
sys.exit(1 if failed else 0)
