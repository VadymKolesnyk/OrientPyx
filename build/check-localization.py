#!/usr/bin/env python3
"""Verify the uk-UA and en-US localization resources stay in sync.

OrientPyx UI text is Ukrainian by default, but every user-facing key must exist in
*both* resource files (CLAUDE.md, "Localization rules"). A missing en-US key is a silent
production bug. This script fails (exit 1) if the two files do not have the exact same key
set, or if either file is not valid JSON / has duplicate keys.

Run standalone:  python build/check-localization.py
It is also wired as a Claude Code Stop hook (.claude/settings.json): on failure it writes
the diagnostics to stderr and exits 2, which blocks the stop and feeds the message back to
Claude to fix. On success it is silent-ish (stdout) and exits 0. Standalone runs read the
same messages on their respective streams.
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

# Exit 2 (not 1) is what a Claude Code Stop hook treats as "block and surface to Claude",
# and it reads *stderr*. Standalone callers can still rely on non-zero == failure.
FAIL = 2

RES = Path(__file__).resolve().parent.parent / "src" / "OrientPyx.Localization" / "Resources"
UK = RES / "uk-UA.json"
EN = RES / "en-US.json"


def load_keys(path: Path) -> set[str] | None:
    """Return the set of top-level keys, or None after printing why it failed (to stderr)."""
    try:
        text = path.read_text(encoding="utf-8")
    except OSError as e:
        print(f"  cannot read {path.name}: {e}", file=sys.stderr)
        return None

    dupes: list[str] = []

    def reject_dupes(pairs):
        seen: dict[str, None] = {}
        for k, _ in pairs:
            if k in seen:
                dupes.append(k)
            seen[k] = None
        return seen

    try:
        data = json.loads(text, object_pairs_hook=reject_dupes)
    except json.JSONDecodeError as e:
        print(f"  {path.name} is not valid JSON: {e}", file=sys.stderr)
        return None

    if dupes:
        print(f"  {path.name} has duplicate keys: {', '.join(sorted(set(dupes)))}", file=sys.stderr)
        return None
    if not isinstance(data, dict):
        print(f"  {path.name} top-level must be a JSON object", file=sys.stderr)
        return None
    return set(data.keys())


def main() -> int:
    uk = load_keys(UK)
    en = load_keys(EN)
    if uk is None or en is None:
        print("Localization check FAILED (see above).", file=sys.stderr)
        return FAIL

    missing_en = sorted(uk - en)  # in uk-UA, absent from en-US
    missing_uk = sorted(en - uk)  # in en-US, absent from uk-UA

    if not missing_en and not missing_uk:
        print(f"Localization OK — {len(uk)} keys, uk-UA and en-US in sync.")
        return 0

    out = sys.stderr
    print("Localization OUT OF SYNC — uk-UA.json and en-US.json have different keys.", file=out)
    if missing_en:
        print(f"\n  Missing from en-US.json ({len(missing_en)}):", file=out)
        for k in missing_en[:50]:
            print(f"    {k}", file=out)
        if len(missing_en) > 50:
            print(f"    … and {len(missing_en) - 50} more", file=out)
    if missing_uk:
        print(f"\n  Missing from uk-UA.json ({len(missing_uk)}):", file=out)
        for k in missing_uk[:50]:
            print(f"    {k}", file=out)
        if len(missing_uk) > 50:
            print(f"    … and {len(missing_uk) - 50} more", file=out)
    print("\nAdd every new user-facing key to BOTH files (CLAUDE.md → Localization rules).", file=out)
    return FAIL


if __name__ == "__main__":
    sys.exit(main())
