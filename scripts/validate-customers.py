#!/usr/bin/env python3
"""
CI gate for customer declarations under infra/customers/.

Three checks, in the order they matter:

  1. Schema validation.
  2. Cross-customer domain collision. Two organisations claiming the same email domain is
     the single most dangerous onboarding error available — it silently routes one
     customer's users to another's IdP. This must fail before any human judgement is
     involved.
  3. Filename/slug agreement, so the file you edit is the organisation you change.

See docs/identity/05-onboarding-runbook.md §5.3.
"""
import json
import pathlib
import sys

import yaml
from jsonschema import Draft202012Validator

ROOT = pathlib.Path(__file__).resolve().parent.parent
CUSTOMERS = ROOT / "infra" / "customers"
SCHEMA = CUSTOMERS / "_schema" / "customer.schema.json"


def main() -> int:
    validator = Draft202012Validator(json.loads(SCHEMA.read_text()))
    files = sorted(p for p in CUSTOMERS.glob("*.yaml"))
    if not files:
        print("No customer declarations found.", file=sys.stderr)
        return 1

    errors: list[str] = []
    domain_owner: dict[str, str] = {}

    for path in files:
        doc = yaml.safe_load(path.read_text())
        slug = (doc.get("metadata") or {}).get("slug", "<missing>")

        # 1. Schema
        for err in sorted(validator.iter_errors(doc), key=lambda e: list(e.path)):
            location = ".".join(str(p) for p in err.path) or "<root>"
            errors.append(f"{path.name}: {location}: {err.message}")

        # 3. Filename must match slug
        if path.stem != slug:
            errors.append(f"{path.name}: filename does not match metadata.slug '{slug}'")

        # 2. Domain collision across ALL customers
        for entry in (doc.get("spec") or {}).get("domains") or []:
            domain = entry.get("domain", "")
            if domain in domain_owner and domain_owner[domain] != slug:
                errors.append(
                    f"{path.name}: DOMAIN COLLISION — '{domain}' is already claimed by "
                    f"'{domain_owner[domain]}'. One customer's users would be routed to "
                    f"another's identity provider."
                )
            domain_owner[domain] = slug

        status = "ok" if not any(path.name in e for e in errors) else "FAIL"
        mode = ((doc.get("spec") or {}).get("authentication") or {}).get("mode", "?")
        print(f"  {status:4} {path.name:32} mode={mode}")

    print()
    if errors:
        print(f"{len(errors)} validation error(s):\n", file=sys.stderr)
        for e in errors:
            print(f"  - {e}", file=sys.stderr)
        return 1

    print(f"{len(files)} customer declaration(s) valid. "
          f"{len(domain_owner)} domain(s), no collisions.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
