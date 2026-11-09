"""Which documentation pages a release may have made stale.

    python website/tools/affected.py            # since the latest v* tag
    python website/tools/affected.py v0.7.15    # since a given ref

Prints (Markdown) for the /new-version docs step:
  * the pages whose code changed (website/docs-map.yml), with the files that triggered them;
  * UI strings whose Ukrainian text changed or disappeared while the old text is still quoted on a page — the docs
    name buttons and menus verbatim, so a renamed button means a wrong page;
  * new UI strings (often a new feature to describe) and changed in-app help texts (Help.*);
  * changed files no page claims (maybe a new feature without a page).
Read-only: it never edits anything.
"""

from __future__ import annotations

import fnmatch
import json
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CONTENT = ROOT / "website" / "content"
STRINGS = "src/OrientPyx.Localization/Resources/uk-UA.json"

# Changes that never concern the user documentation.
IGNORED = ["tests/*", "website/*", "src/OrientPyx.Localization/Resources/en-US.json", "docs/*-plan.md", "*.csproj", "*/Migrations/*", ".claude/*", "CLAUDE.md",
           "scripts/*", "*.sln", "README.md", "LICENSE", ".gitignore", "*/obj/*", "*/bin/*"]


def git(*args: str) -> str:
    return subprocess.run(["git", *args], cwd=ROOT, check=True, capture_output=True, text=True, encoding="utf-8").stdout


def load_map() -> dict[str, list[str]]:
    """website/docs-map.yml without a YAML dependency: «page:» lines followed by «- pattern» lines."""
    pages: dict[str, list[str]] = {}
    current = None
    for raw in (ROOT / "website" / "docs-map.yml").read_text(encoding="utf-8").splitlines():
        line = raw.split("#", 1)[0].rstrip()
        if not line.strip() or line.strip() == "pages:":
            continue
        m = re.fullmatch(r"\s{2}(\S+\.md):", line)
        if m:
            current = m.group(1)
            pages[current] = []
            continue
        m = re.fullmatch(r"\s{4}-\s+(.+)", line)
        if m and current:
            pages[current].append(m.group(1).strip())
    return pages


def strings_at(ref: str) -> dict[str, str]:
    try:
        return json.loads(git("show", f"{ref}:{STRINGS}"))
    except subprocess.CalledProcessError:
        return {}


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8")   # Windows consoles default to a legacy code page
    base = sys.argv[1] if len(sys.argv) > 1 else git("describe", "--tags", "--abbrev=0", "--match", "v*").strip()
    changed = [f for f in git("diff", "--name-only", f"{base}...HEAD").splitlines() if f]
    changed += [f for f in git("diff", "--name-only", "HEAD").splitlines() if f and f not in changed]   # uncommitted
    docs_map = load_map()

    print(f"# Documentation check since {base}\n")
    print(f"{len(changed)} changed files.\n")

    affected: dict[str, list[str]] = {}
    unclaimed = []
    for path in changed:
        hits = [page for page, patterns in docs_map.items() if any(fnmatch.fnmatch(path, p) for p in patterns)]
        for page in hits:
            affected.setdefault(page, []).append(path)
        if not hits and not any(fnmatch.fnmatch(path, p) for p in IGNORED) and path != STRINGS:
            unclaimed.append(path)

    print("## Pages to re-read\n")
    if not affected:
        print("None.\n")
    for page in sorted(affected):
        files = affected[page]
        shown = ", ".join(f"`{f}`" for f in files[:6]) + (f" and {len(files) - 6} more" if len(files) > 6 else "")
        print(f"- `website/content/{page}` ← {shown}")
    print()

    old, new = strings_at(base), json.loads((ROOT / STRINGS).read_text(encoding="utf-8"))
    pages_text = {p.relative_to(CONTENT).as_posix(): p.read_text(encoding="utf-8") for p in CONTENT.rglob("*.md")}

    print("## Renamed or removed UI text still quoted in the docs\n")
    stale = 0
    for key, before in old.items():
        if key.startswith("Help.") or len(before) < 3 or new.get(key) == before:
            continue
        quoted = f"«{before}»"
        where = [page for page, text in pages_text.items() if quoted in text]
        if where:
            stale += 1
            after = new.get(key)
            change = f"«{before}» → «{after}»" if after is not None else f"«{before}» (removed)"
            print(f"- `{key}`: {change} — in " + ", ".join(f"`{w}`" for w in where))
    if not stale:
        print("None.")
    print()

    added = [k for k in new if k not in old and not k.startswith("Help.")]
    print("## New UI strings (a new feature to describe?)\n")
    print("\n".join(f"- `{k}`: «{new[k]}»" for k in added) or "None.")
    print()

    help_changed = sorted({k.rsplit(".", 1)[0] for k in new if k.startswith("Help.") and old.get(k) != new[k]})
    print("## In-app help texts changed (the «Довідник екранів» follows them by itself; check the guides agree)\n")
    print("\n".join(f"- `{k}`" for k in help_changed) or "None.")
    print()

    print("## Changed files no page claims\n")
    print("\n".join(f"- `{f}`" for f in unclaimed) or "None.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
