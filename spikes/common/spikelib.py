"""
Shared harness for the Phase 0 spikes.

Three jobs, in order of importance:

1. **Refuse to touch anything that is not a disposable spike tenant.** There is no override
   flag. These scripts create and delete directory objects; pointing one at production would
   be a live incident, so the guard is structural.

2. **Capture evidence.** Every Graph request and response is written to a JSONL transcript
   including the ``request-id`` header, which is what you quote when escalating to Microsoft
   support. A spike whose findings cannot be reproduced six weeks later did not happen.

3. **Talk to Graph over raw REST**, deliberately. The SDKs wrap error bodies, and for R1 the
   exact unvarnished error is the entire finding.
"""

from __future__ import annotations

import json
import os
import pathlib
import sys
import time
import uuid
from dataclasses import dataclass, field
from datetime import datetime, timezone
from typing import Any

import requests

REPO_ROOT = pathlib.Path(__file__).resolve().parent.parent.parent
SPIKES_DIR = REPO_ROOT / "spikes"
CONFIG_PATH = SPIKES_DIR / "spike-config.json"
EVIDENCE_ROOT = SPIKES_DIR / "evidence"

GRAPH = "https://graph.microsoft.com"


class SpikeSafetyError(RuntimeError):
    """Raised when a script is pointed at something it must not touch."""


class SpikeConfigError(RuntimeError):
    """Raised when spike-config.json is missing or incomplete."""


# --------------------------------------------------------------------------- config


def load_config() -> dict[str, Any]:
    if not CONFIG_PATH.exists():
        raise SpikeConfigError(
            f"{CONFIG_PATH} not found. Copy spike-config.example.json to spike-config.json "
            "and fill it in. It is gitignored."
        )
    return json.loads(CONFIG_PATH.read_text())


@dataclass(frozen=True)
class SpikeTenant:
    name: str
    tenant_id: str
    domain: str
    kind: str            # external | workforce
    authority: str
    client_id: str
    client_secret: str
    verified_custom_domain: str | None = None


def get_tenant(config: dict[str, Any], name: str) -> SpikeTenant:
    """
    Resolve a named tenant from config, enforcing the disposability guard.

    The guard is the reason this function exists rather than callers reading the dict
    directly. Every path to a tenant goes through here.
    """
    tenants = config.get("spike_tenants") or {}
    if name not in tenants:
        raise SpikeConfigError(
            f"Tenant '{name}' is not in spike_tenants. Known: {sorted(tenants) or '(none)'}"
        )

    entry = tenants[name]

    # ---- the guard ---------------------------------------------------------
    if entry.get("disposable") is not True:
        raise SpikeSafetyError(
            f"Tenant '{name}' ({entry.get('tenant_id')}) is not marked "
            f'"disposable": true in spike-config.json.\n'
            "These scripts create and delete directory objects. Mark the tenant disposable "
            "only if losing everything in it would be a non-event. There is no override."
        )

    tenant_id = entry.get("tenant_id", "")
    if not tenant_id or tenant_id.startswith("00000000-0000"):
        raise SpikeConfigError(
            f"Tenant '{name}' still has the placeholder tenant_id from the example config."
        )

    secret_env = entry.get("client_secret_env")
    if not secret_env:
        raise SpikeConfigError(f"Tenant '{name}' has no client_secret_env.")
    secret = os.environ.get(secret_env)
    if not secret:
        raise SpikeConfigError(
            f"Environment variable {secret_env} is not set (needed for tenant '{name}')."
        )

    return SpikeTenant(
        name=name,
        tenant_id=tenant_id,
        domain=entry["domain"],
        kind=entry.get("kind", "external"),
        authority=entry.get("authority", "https://login.microsoftonline.com"),
        client_id=entry["client_id"],
        client_secret=secret,
        verified_custom_domain=entry.get("verified_custom_domain"),
    )


# --------------------------------------------------------------------------- evidence


@dataclass
class Evidence:
    """
    Append-only transcript for one spike run.

    Writes JSONL (machine-readable, one event per line) plus a markdown summary you can
    paste into RESULTS.md or a support ticket.
    """

    spike: str
    run_dir: pathlib.Path = field(init=False)
    _jsonl: Any = field(init=False, repr=False)
    _events: list[dict[str, Any]] = field(default_factory=list, init=False)

    def __post_init__(self) -> None:
        stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
        self.run_dir = EVIDENCE_ROOT / self.spike / stamp
        self.run_dir.mkdir(parents=True, exist_ok=True)
        self._jsonl = (self.run_dir / "transcript.jsonl").open("a", encoding="utf-8")
        self.note("run_started", spike=self.spike, utc=stamp)

    def note(self, kind: str, **fields: Any) -> dict[str, Any]:
        event = {
            "ts": datetime.now(timezone.utc).isoformat(),
            "kind": kind,
            **fields,
        }
        self._events.append(event)
        self._jsonl.write(json.dumps(event, default=str) + "\n")
        self._jsonl.flush()
        return event

    def events(self, kind: str | None = None) -> list[dict[str, Any]]:
        return [e for e in self._events if kind is None or e["kind"] == kind]

    def write_summary(self, title: str, body: str) -> pathlib.Path:
        path = self.run_dir / "SUMMARY.md"
        path.write_text(f"# {title}\n\n_Generated {datetime.now(timezone.utc).isoformat()}_\n\n{body}\n")
        return path

    def close(self) -> None:
        self.note("run_finished")
        self._jsonl.close()

    def __enter__(self) -> "Evidence":
        return self

    def __exit__(self, *exc: Any) -> None:
        self.close()


# --------------------------------------------------------------------------- graph


class GraphClient:
    """
    Minimal Graph REST client that records everything.

    Deliberately not the SDK: for R1 the exact error body and the ``request-id`` header are
    the finding, and SDKs normalise both away.
    """

    def __init__(self, tenant: SpikeTenant, evidence: Evidence, api_version: str = "v1.0"):
        self.tenant = tenant
        self.evidence = evidence
        self.api_version = api_version
        self._token: str | None = None
        self._token_expiry: float = 0.0

    # -- auth ---------------------------------------------------------------
    def _access_token(self) -> str:
        if self._token and time.time() < self._token_expiry - 60:
            return self._token

        url = f"https://login.microsoftonline.com/{self.tenant.tenant_id}/oauth2/v2.0/token"
        resp = requests.post(
            url,
            data={
                "client_id": self.tenant.client_id,
                "client_secret": self.tenant.client_secret,
                "scope": f"{GRAPH}/.default",
                "grant_type": "client_credentials",
            },
            timeout=30,
        )
        if resp.status_code != 200:
            self.evidence.note(
                "token_failure",
                tenant=self.tenant.name,
                status=resp.status_code,
                body=_safe_json(resp),
            )
            resp.raise_for_status()

        payload = resp.json()
        self._token = payload["access_token"]
        self._token_expiry = time.time() + int(payload.get("expires_in", 3600))
        self.evidence.note("token_acquired", tenant=self.tenant.name, expires_in=payload.get("expires_in"))
        return self._token

    # -- requests -----------------------------------------------------------
    def request(
        self,
        method: str,
        path: str,
        *,
        json_body: dict[str, Any] | None = None,
        api_version: str | None = None,
        label: str | None = None,
    ) -> "GraphResult":
        version = api_version or self.api_version
        url = path if path.startswith("http") else f"{GRAPH}/{version}/{path.lstrip('/')}"
        client_request_id = str(uuid.uuid4())

        started = time.perf_counter()
        resp = requests.request(
            method,
            url,
            headers={
                "Authorization": f"Bearer {self._access_token()}",
                "Content-Type": "application/json",
                # Echoed back by Graph. Quote it to Microsoft support alongside request-id.
                "client-request-id": client_request_id,
            },
            json=json_body,
            timeout=60,
        )
        elapsed_ms = round((time.perf_counter() - started) * 1000, 1)

        result = GraphResult(
            label=label or f"{method} {path}",
            method=method,
            url=url,
            status=resp.status_code,
            body=_safe_json(resp),
            request_id=resp.headers.get("request-id", ""),
            client_request_id=client_request_id,
            graph_date=resp.headers.get("Date", ""),
            elapsed_ms=elapsed_ms,
            api_version=version,
        )

        self.evidence.note(
            "graph_call",
            label=result.label,
            method=method,
            url=url,
            api_version=version,
            request_body=json_body,
            status=result.status,
            response_body=result.body,
            request_id=result.request_id,
            client_request_id=client_request_id,
            elapsed_ms=elapsed_ms,
        )
        return result

    def get(self, path: str, **kw: Any) -> "GraphResult":
        return self.request("GET", path, **kw)

    def post(self, path: str, json_body: dict[str, Any], **kw: Any) -> "GraphResult":
        return self.request("POST", path, json_body=json_body, **kw)

    def delete(self, path: str, **kw: Any) -> "GraphResult":
        return self.request("DELETE", path, **kw)


@dataclass
class GraphResult:
    label: str
    method: str
    url: str
    status: int
    body: Any
    request_id: str
    client_request_id: str
    graph_date: str
    elapsed_ms: float
    api_version: str

    @property
    def ok(self) -> bool:
        return 200 <= self.status < 300

    @property
    def error_code(self) -> str:
        if isinstance(self.body, dict):
            err = self.body.get("error")
            if isinstance(err, dict):
                return str(err.get("code", ""))
        return ""

    @property
    def error_message(self) -> str:
        if isinstance(self.body, dict):
            err = self.body.get("error")
            if isinstance(err, dict):
                return str(err.get("message", ""))
        return ""

    def one_line(self) -> str:
        if self.ok:
            return f"{self.status} OK ({self.elapsed_ms}ms)"
        return f"{self.status} {self.error_code}: {_truncate(self.error_message, 160)}"


# --------------------------------------------------------------------------- helpers


def _safe_json(resp: requests.Response) -> Any:
    try:
        return resp.json()
    except ValueError:
        return {"_raw": resp.text[:4000]}


def _truncate(text: str, limit: int) -> str:
    text = " ".join(text.split())
    return text if len(text) <= limit else text[: limit - 1] + "…"


def banner(title: str, subtitle: str = "") -> None:
    print()
    print("=" * 72)
    print(f"  {title}")
    if subtitle:
        print(f"  {subtitle}")
    print("=" * 72)


def fail(message: str, code: int = 1) -> None:
    print(f"\nERROR: {message}\n", file=sys.stderr)
    raise SystemExit(code)
