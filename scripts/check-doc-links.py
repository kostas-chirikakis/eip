#!/usr/bin/env python3
"""
Verifies every internal markdown link and heading anchor resolves.

The design documents cross-reference each other heavily — a claim in the APIM document
points at the risk register entry that qualifies it. A broken anchor silently detaches a
mitigation from the risk it mitigates, which is exactly the kind of rot that makes a design
document untrustworthy six months in.
"""
import pathlib
import re
import sys
import urllib.parse

ROOT = pathlib.Path(__file__).resolve().parent.parent
SKIP_DIRS = {".git", "node_modules", "bin", "obj"}


def anchors(path: pathlib.Path) -> set[str]:
    """GitHub's algorithm: downcase, strip non [word/space/hyphen], each space -> one hyphen."""
    found = set()
    for line in path.read_text().splitlines():
        m = re.match(r"^#{1,6}\s+(.*)", line)
        if not m:
            continue
        text = re.sub(r"[`*_]", "", m.group(1).strip().lower())
        text = re.sub(r"[^\w\s-]", "", text)
        found.add(text.replace(" ", "-"))   # deliberately not collapsed
    return found


def main() -> int:
    files = sorted(
        p for p in ROOT.rglob("*.md")
        if not SKIP_DIRS.intersection(p.parts)
    )
    broken: list[str] = []

    for f in files:
        for m in re.finditer(r"\[([^\]]*)\]\(([^)]+)\)", f.read_text()):
            target = m.group(2).strip()
            if target.startswith(("http://", "https://", "mailto:")):
                continue

            if target.startswith("#"):
                if target[1:] not in anchors(f):
                    broken.append(f"{f.relative_to(ROOT)}: same-file anchor '{target}'")
                continue

            path_part, _, frag = target.partition("#")
            resolved = (f.parent / urllib.parse.unquote(path_part)).resolve()
            if not resolved.exists():
                broken.append(f"{f.relative_to(ROOT)}: missing file -> {target}")
            elif frag and resolved.suffix == ".md" and frag not in anchors(resolved):
                broken.append(f"{f.relative_to(ROOT)}: missing anchor -> {target}")

    print(f"checked {len(files)} markdown file(s)")
    if broken:
        print(f"\n{len(broken)} broken link(s):", file=sys.stderr)
        for b in broken:
            print(f"  - {b}", file=sys.stderr)
        return 1

    print("all internal links and anchors resolve")
    return 0


if __name__ == "__main__":
    sys.exit(main())
