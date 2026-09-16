"""
R4 — can one customer's user enumerate another's?

    python -m spikes.r4_directory_enumeration.run_probes

This answers a question a pharma customer's security questionnaire **will** ask and which we
currently cannot answer with evidence. External tenants restrict directory read far more than
workforce tenants — but that must be verified by test, not assumed from documentation.

## Why this uses a delegated token, not an app-only one

Our provisioning service principal deliberately holds tenant-wide Graph permissions, so an
app-only token proves nothing about what a *user* can do. The threat is a customer's end
user, so the probe must run as one. That means device-code flow and a real sign-in.

## Severity, stated plainly

A demonstrated cross-customer enumeration is a reportable incident and a failed security
review, even if no tender data leaks. In Life Sciences, the customer list itself is
commercially sensitive: knowing which pharma companies run Cube RM is valuable to a
competitor independent of any document they hold.
"""

from __future__ import annotations

import sys
import time

import requests

from ..common.spikelib import (
    Evidence,
    SpikeConfigError,
    SpikeSafetyError,
    banner,
    fail,
    get_tenant,
    load_config,
)

GRAPH = "https://graph.microsoft.com"

# Each probe is (label, path, what a leak would mean). Ordered from broadest to narrowest,
# because a broad success makes the narrow ones moot.
PROBES: list[tuple[str, str, str]] = [
    ("list all users", "/v1.0/users?$top=999",
     "Full tenant enumeration. Every customer's user list readable by any user."),
    ("list users, select mail", "/v1.0/users?$select=mail,displayName&$top=999",
     "Same, with contact details attached."),
    ("filter users by domain", "/v1.0/users?$filter=endswith(mail,'.com')&$count=true",
     "Targeted enumeration — pick a competitor's domain and list their staff."),
    ("list groups", "/v1.0/groups?$top=999",
     "Operational group names leak the customer roster via org.{slug}.members."),
    ("read own memberOf", "/v1.0/me/memberOf",
     "Expected to succeed. Only a leak if it returns groups from other organisations."),
    ("list directory objects", "/v1.0/directoryObjects?$top=50",
     "Broadest possible read."),
    ("list applications", "/v1.0/applications?$top=50",
     "App registrations reveal platform topology."),
    ("list identity providers", "/v1.0/identity/identityProviders",
     "CRITICAL — this is the full list of every customer's federated IdP configuration."),
    ("read own profile", "/v1.0/me",
     "Control. Must succeed, or the token is wrong and every other result is meaningless."),
]


def device_code_login(tenant_id: str, client_id: str, evidence: Evidence) -> str | None:
    """
    Delegated sign-in via device code, so the probe runs with a real user's permissions.
    """
    resp = requests.post(
        f"https://login.microsoftonline.com/{tenant_id}/oauth2/v2.0/devicecode",
        data={"client_id": client_id, "scope": "openid profile User.Read"},
        timeout=30,
    )
    if resp.status_code != 200:
        evidence.note("devicecode_failed", status=resp.status_code, body=resp.text[:1500])
        print(f"   Could not start device code flow: {resp.status_code} {resp.text[:300]}")
        print("   The app registration needs 'Allow public client flows' enabled.")
        return None

    flow = resp.json()
    print()
    print("   " + "-" * 64)
    print(f"   {flow['message']}")
    print("   " + "-" * 64)
    print()
    print("   Sign in as a user belonging to ORG A only.")
    print("   Waiting…")

    deadline = time.time() + int(flow.get("expires_in", 900))
    interval = int(flow.get("interval", 5))

    while time.time() < deadline:
        time.sleep(interval)
        poll = requests.post(
            f"https://login.microsoftonline.com/{tenant_id}/oauth2/v2.0/token",
            data={
                "grant_type": "urn:ietf:params:oauth:grant-type:device_code",
                "client_id": client_id,
                "device_code": flow["device_code"],
            },
            timeout=30,
        )
        if poll.status_code == 200:
            evidence.note("devicecode_success")
            return poll.json()["access_token"]

        err = poll.json().get("error", "")
        if err == "authorization_pending":
            continue
        if err == "slow_down":
            interval += 5
            continue
        evidence.note("devicecode_error", error=err, body=poll.text[:1500])
        print(f"   Device code flow failed: {err}")
        return None

    print("   Timed out waiting for sign-in.")
    return None


def main() -> int:
    try:
        config = load_config()
        ciam = get_tenant(config, "spike_ciam")
    except (SpikeConfigError, SpikeSafetyError) as exc:
        fail(str(exc))
        return 1

    banner("R4 — directory enumeration", f"tenant {ciam.tenant_id}")
    print("  Running as a DELEGATED user token. An app-only token would prove nothing:")
    print("  the threat is a customer's end user, not our provisioning principal.")

    findings: list[dict] = []

    with Evidence("r4_directory_enumeration") as ev:
        ev.note("context", tenant=ciam.tenant_id)

        print("\n1. Delegated sign-in")
        token = device_code_login(ciam.tenant_id, ciam.client_id, ev)
        if not token:
            fail("Could not obtain a delegated token. R4 cannot run without one.")
            return 1

        print("\n2. Probes")
        headers = {"Authorization": f"Bearer {token}", "ConsistencyLevel": "eventual"}

        for label, path, meaning in PROBES:
            resp = requests.get(f"{GRAPH}{path}", headers=headers, timeout=45)
            body: object
            try:
                body = resp.json()
            except ValueError:
                body = {"_raw": resp.text[:1500]}

            count = None
            if resp.status_code == 200 and isinstance(body, dict) and isinstance(body.get("value"), list):
                count = len(body["value"])

            is_control = label == "read own profile"
            leaked = resp.status_code == 200 and not is_control and (count is None or count > 0)

            ev.note(
                "probe",
                label=label,
                path=path,
                status=resp.status_code,
                item_count=count,
                leaked=leaked,
                request_id=resp.headers.get("request-id", ""),
                response_sample=str(body)[:1200],
            )

            if resp.status_code == 200:
                mark = "LEAK" if leaked else "ok  "
                detail = f"200, {count} item(s)" if count is not None else "200"
            else:
                mark = "ok  "
                err = body.get("error", {}).get("code", "") if isinstance(body, dict) else ""
                detail = f"{resp.status_code} {err}"

            print(f"   [{mark}] {label:<28} {detail}")
            if leaked:
                print(f"          → {meaning}")

            findings.append({
                "label": label, "path": path, "status": resp.status_code,
                "count": count, "leaked": leaked, "meaning": meaning,
                "is_control": is_control,
            })

        control = next((f for f in findings if f["is_control"]), None)
        leaks = [f for f in findings if f["leaked"]]

        summary = render_summary(findings, ciam.tenant_id, control, leaks)
        path_ = ev.write_summary("R4 — directory enumeration", summary)

    print("\n" + "=" * 72)
    if control and control["status"] != 200:
        print("  ⛔ THE CONTROL FAILED (/me did not return 200).")
        print("     The token is wrong. No other result here means anything.")
        return 1
    if leaks:
        print(f"  {len(leaks)} ENUMERATION LEAK(S) FOUND — this is a P1 finding.")
        print("  Do not proceed to Phase 2 without closing these.")
    else:
        print("  No enumeration leaks detected.")
        print("  Promote these probes to a CI regression test against non-prod — tenant defaults")
        print("  change, and this answer has a shelf life.")
    print(f"\n  Evidence: {path_.parent}")
    return 1 if leaks else 0


def render_summary(findings, tenant_id, control, leaks) -> str:
    lines = [
        f"Tenant `{tenant_id}` · delegated user token",
        "",
        "| Probe | Status | Items | Verdict |",
        "| --- | --- | --- | --- |",
    ]
    for f in findings:
        verdict = "**LEAK**" if f["leaked"] else ("control" if f["is_control"] else "blocked")
        lines.append(f"| {f['label']} | {f['status']} | {f['count'] if f['count'] is not None else '—'} | {verdict} |")

    lines += ["", "## Verdict", ""]
    if control and control["status"] != 200:
        lines.append("⛔ **Control failed** — `/me` did not return 200, so the token is wrong and no result is interpretable.")
    elif leaks:
        lines.append(f"**{len(leaks)} enumeration leak(s).** P1. Each one below is answerable in a customer security questionnaire only as 'yes, and we have not fixed it'.")
        lines.append("")
        for f in leaks:
            lines.append(f"- `{f['label']}` → {f['meaning']}")
        lines += [
            "",
            "Remediation order: tenant default user permissions first (it closes most of these at",
            "once), then app registration scopes — no user-delegated Graph permission should ever",
            "be requested by a Cube RM application.",
        ]
    else:
        lines.append("No leaks detected with this token. Two caveats before treating it as settled:")
        lines.append("")
        lines.append("- Tenant defaults change. This needs to be a CI regression test, not a one-off.")
        lines.append("- Test again with a token carrying every scope our real apps request — a")
        lines.append("  narrower spike token can under-report.")
    return "\n".join(lines)


if __name__ == "__main__":
    sys.exit(main())
