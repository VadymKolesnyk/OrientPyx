"""MkDocs hooks of the OrientPyx documentation site.

* «Довідник екранів» — one page per screen, generated on every build from the app's own help texts (the «?» button:
  `Help.<Screen>.Title/What/Why/How` in src/OrientPyx.Localization/Resources/uk-UA.json, block helps
  `Help.<Screen>.<Block>.*` as sections). Nothing is written to disk, so the site can never disagree with the app.
  The URL rule — screens/<kebab Screen>/#<kebab Block> — is mirrored by the app's «Детальніше в документації» link
  (src/OrientPyx.Presentation/Services/DocsLinks.cs); keep both in step.
* «Що нового» — the release notes of every GitHub release (falls back to the page's own text when offline).
"""

from __future__ import annotations

import html
import json
import logging
import os
import re
import urllib.request
from pathlib import Path

from mkdocs.structure.files import File

log = logging.getLogger("mkdocs.hooks.orientpyx")

RESOURCES = Path(__file__).resolve().parent.parent / "src" / "OrientPyx.Localization" / "Resources" / "uk-UA.json"
REPO = "VadymKolesnyk/OrientPyx"

# Screen → where it is in the app (localization keys of the menu path), its screenshot, the guide pages that cover it.
# The order is the order of the «Довідник екранів» section. A help prefix missing here still gets a page (at the end).
SCREENS: dict[str, dict] = {
    "Dashboard": {"menu": ["Nav.Dashboard"], "shot": "dashboard", "see": ["start/overview.md"]},
    "CompetitionInfo": {"menu": ["Menu.Competition", "Menu.Competition.Info"], "shot": "competition-info", "see": ["prepare/competition.md"]},
    "CompetitionDays": {"menu": ["Menu.Competition", "Menu.Competition.Days"], "shot": "competition-days", "see": ["prepare/competition.md", "guides/index.md"]},
    "ControlPoints": {"menu": ["Menu.Competition", "Menu.Competition.ControlPoints"], "shot": "control-points", "see": ["prepare/courses.md"]},
    "Groups": {"menu": ["Menu.Competition", "Menu.Competition.Groups"], "shot": "groups", "see": ["prepare/courses.md"]},
    "Chips": {"menu": ["Menu.Competition", "Menu.Competition.Chips"], "shot": "chips", "see": ["prepare/chips.md"]},
    "RelayTeams": {"menu": ["Menu.Competition", "Menu.Competition.RelayTeams"], "shot": "relay-teams", "see": ["guides/relay.md"]},
    "Regions": {"menu": ["Menu.Competition", "Menu.Competition.Directories", "Menu.Competition.Regions"], "shot": "regions", "see": ["prepare/participants.md"]},
    "Clubs": {"menu": ["Menu.Competition", "Menu.Competition.Directories", "Menu.Competition.Clubs"], "shot": "clubs", "see": ["prepare/participants.md"]},
    "Dussh": {"menu": ["Menu.Competition", "Menu.Competition.Directories", "Menu.Competition.Dussh"], "shot": "dussh", "see": ["prepare/participants.md"]},
    "EntryFees": {"menu": ["Menu.Competition", "Menu.Competition.Directories", "Menu.Participants.EntryFees"], "shot": "entry-fees", "see": ["prepare/entry-fees.md"]},
    "Ranks": {"menu": ["Menu.Competition", "Menu.Competition.Qualification", "Menu.Competition.Ranks"], "shot": "ranks", "see": ["after/points-ranks.md"]},
    "Points": {"menu": ["Menu.Competition", "Menu.Competition.Qualification", "Menu.Competition.Points"], "shot": "points", "see": ["after/points-ranks.md"]},
    "Participants": {"menu": ["Menu.Participants"], "shot": "participants-day", "see": ["prepare/participants.md", "prepare/chips.md"]},
    "Draw": {"menu": ["Menu.Start", "Menu.Start.Draw"], "shot": "draw", "see": ["prepare/draw.md"]},
    "ClassicDraw": {"menu": ["Menu.Start", "Menu.Start.ClassicDraw"], "shot": "classic-draw", "see": ["prepare/draw.md"]},
    "MassStartDraw": {"menu": ["Menu.Start", "Menu.Start.MassStartDraw"], "shot": "mass-start-draw", "see": ["prepare/draw.md"]},
    "StartProtocols": {"menu": ["Menu.Start", "Menu.Start.StartProtocol"], "shot": "start-protocol", "see": ["prepare/draw.md"]},
    "MapCounts": {"menu": ["Menu.Start", "Menu.Start.MapCounts"], "shot": "map-counts", "see": ["prepare/draw.md"]},
    "FinishRead": {"menu": ["Menu.Finish", "Menu.Finish.Read"], "shot": "finish-read", "see": ["race-day/finish.md"]},
    "Protocols": {"menu": ["Menu.Protocols", "Menu.Protocols.Results"], "shot": "protocols", "see": ["race-day/results.md"]},
    "ProtocolLayout": {"menu": ["Menu.Protocols", "Menu.Protocols.Results"], "see": ["race-day/results.md"]},
    "ProtocolPreview": {"menu": ["Menu.Protocols", "Menu.Protocols.Results"], "shot": "protocols-preview", "see": ["race-day/results.md"]},
    "SummaryProtocol": {"menu": ["Menu.Protocols", "Menu.Protocols.Summary"], "shot": "summary-protocol", "see": ["after/summary.md"]},
    "Splits": {"menu": ["Menu.Protocols", "Menu.Protocols.Splits"], "shot": "splits-export", "see": ["race-day/results.md"]},
    "Monitor": {"menu": ["Menu.Protocols", "Menu.Protocols.Monitor"], "shot": "monitor", "see": ["race-day/monitor-online.md"]},
    "OnlineResults": {"menu": ["Menu.Protocols", "Menu.Protocols.OnlineResults"], "shot": "online-results", "see": ["race-day/monitor-online.md"]},
    "Plugins": {"menu": ["Menu.Plugins", "Menu.Plugins.Manage"], "shot": "plugins", "see": ["guides/standard-plugins.md"]},
    "PluginSettings": {"menu": ["Menu.Plugins"], "shot": "plugin-settings", "see": ["guides/standard-plugins.md"]},
    "PluginReport": {"menu": ["Menu.Plugins"], "see": ["guides/standard-plugins.md", "plugins/api.md"]},
    "Settings": {"menu": ["Menu.File", "Menu.File.Settings"], "shot": "settings", "see": ["start/overview.md"]},
    "Network": {"menu": ["Menu.File", "Menu.File.Network"], "shot": "network", "see": ["race-day/network.md"]},
}

SECTION = "Довідник екранів"


def kebab(name: str) -> str:
    """CompetitionInfo → competition-info (the URL rule shared with the app's DocsLinks)."""
    return re.sub(r"(?<=[a-z0-9])(?=[A-Z])", "-", name).lower()


def _load_strings() -> dict[str, str]:
    with RESOURCES.open(encoding="utf-8") as f:
        return json.load(f)


def _screens(strings: dict[str, str]) -> list[tuple[str, list[str]]]:
    """[(screen, [block, …])] for every Help.<Screen>.Title, in SCREENS order, then the rest alphabetically."""
    tops: list[str] = []
    blocks: dict[str, list[str]] = {}
    for key in strings:
        m = re.fullmatch(r"Help\.([A-Za-z]+)\.Title", key)
        if m:
            tops.append(m.group(1))
            continue
        m = re.fullmatch(r"Help\.([A-Za-z]+)\.([A-Za-z]+)\.Title", key)
        if m:
            blocks.setdefault(m.group(1), []).append(m.group(2))
    order = [s for s in SCREENS if s in tops] + sorted(s for s in tops if s not in SCREENS)
    return [(s, blocks.get(s, [])) for s in order]


def _text(value: str) -> str:
    """A help string as Markdown text: HTML-escaped, Markdown metacharacters neutralised."""
    value = html.escape(value, quote=False)
    return re.sub(r"([\\`*_\[\]])", r"\\\1", value)


def _how(value: str) -> str:
    lines = [line.strip() for line in value.split("\n") if line.strip()]
    out = []
    for line in lines:
        line = _text(line)
        line = re.sub(r"^(\d+)\.(\s)", r"\1\\.\2", line)   # «1. …» stays text inside the bullet
        out.append(f"- {line}")
    return "\n".join(out)


def _body(strings: dict[str, str], prefix: str, level: str) -> str:
    def get(name: str) -> str:
        return strings.get(f"{prefix}.{name}", "").strip()

    parts = []
    for name, label_key in (("What", "Help.Section.What"), ("Why", "Help.Section.Why")):
        if get(name):
            parts.append(f"{level} {_text(strings.get(label_key, name))}\n\n{_text(get(name))}")
    if get("How"):
        parts.append(f"{level} {_text(strings.get('Help.Section.How', 'How'))}\n\n{_how(get('How'))}")
    return "\n\n".join(parts)


def _page(strings: dict[str, str], screen: str, blocks: list[str]) -> str:
    meta = SCREENS.get(screen, {})
    prefix = f"Help.{screen}"
    out = [f"# {_text(strings[prefix + '.Title'])}", ""]
    menu = [strings.get(k, k) for k in meta.get("menu", [])]
    if menu:
        out += [f'<span class="menu">{html.escape(" → ".join(menu))}</span>', ""]
    if meta.get("shot"):
        out += [f"![{_text(strings[prefix + '.Title'])}](../shots/{meta['shot']}.png){{ .shot }}", ""]
    out += [_body(strings, prefix, "##"), ""]
    for block in blocks:
        bp = f"{prefix}.{block}"
        out += [f"## {_text(strings[bp + '.Title'])} {{#{kebab(block)}}}", "", _body(strings, bp, "###"), ""]
    if meta.get("see"):
        out += ["## Детальніше", ""]
        out += [f"- [{_title_of(page)}](../{page})" for page in meta["see"]]
        out.append("")
    out += ["!!! note \"Звідки цей текст\"",
            "    Це та сама довідка, що відкривається кнопкою **?** на цьому екрані програми."]
    return "\n".join(out) + "\n"


_TITLES: dict[str, str] = {}


def _title_of(src_uri: str) -> str:
    return _TITLES.get(src_uri, src_uri)


# ---- hooks ------------------------------------------------------------------------------------------------------------

_SCREEN_LIST: list[tuple[str, list[str]]] = []
_STRINGS: dict[str, str] = {}


def on_config(config, **_):
    """Adds the generated screen pages to the «Довідник екранів» section of the navigation."""
    global _SCREEN_LIST, _STRINGS
    _STRINGS = _load_strings()
    _SCREEN_LIST = _screens(_STRINGS)
    pages = [f"screens/{kebab(s)}.md" for s, _ in _SCREEN_LIST]
    for i, item in enumerate(config["nav"]):
        if isinstance(item, dict) and SECTION in item:
            config["nav"][i] = {SECTION: ["screens/index.md", *pages]}
            break
    return config


def on_files(files, config, **_):
    for screen, blocks in _SCREEN_LIST:
        files.append(File.generated(config, f"screens/{kebab(screen)}.md", content=_page(_STRINGS, screen, blocks)))
    return files


def on_nav(nav, config, files, **_):
    for page in nav.pages:
        if page.title:
            _TITLES[page.file.src_uri] = page.title
    return nav


def on_page_markdown(markdown, page, config, files, **_):
    src = page.file.src_uri
    if src.startswith("screens/") and src != "screens/index.md":
        # Titles of the guide pages are known only once the navigation is built: re-render the «Детальніше» list.
        screen = next((s for s, _ in _SCREEN_LIST if f"screens/{kebab(s)}.md" == src), None)
        if screen:
            return _page(_STRINGS, screen, dict(_SCREEN_LIST)[screen])
    if src == "screens/index.md":
        rows = ["| Екран | Де в програмі |", "|---|---|"]
        for screen, _ in _SCREEN_LIST:
            menu = " → ".join(_STRINGS.get(k, k) for k in SCREENS.get(screen, {}).get("menu", []))
            rows.append(f"| [{_text(_STRINGS['Help.' + screen + '.Title'])}]({kebab(screen)}.md) | {html.escape(menu)} |")
        return markdown.rstrip() + "\n\n" + "\n".join(rows) + "\n"
    if src == "changelog.md":
        return _changelog() or markdown
    return markdown


_CHANGELOG: str | None = None


def _changelog() -> str | None:
    """«Що нового» from the GitHub releases (cached per build process); None when GitHub is unreachable."""
    global _CHANGELOG
    if _CHANGELOG is not None:
        return _CHANGELOG or None
    try:
        request = urllib.request.Request(f"https://api.github.com/repos/{REPO}/releases?per_page=100",
                                         headers={"Accept": "application/vnd.github+json", "User-Agent": "orientpyx-docs"})
        token = os.environ.get("GITHUB_TOKEN")
        if token:
            request.add_header("Authorization", f"Bearer {token}")
        with urllib.request.urlopen(request, timeout=15) as response:
            releases = json.load(response)
    except Exception as error:  # offline preview, rate limit: keep the page's own text
        log.info("Changelog: GitHub releases unavailable (%s); using changelog.md as is.", error)
        _CHANGELOG = ""
        return None

    out = ["# Що нового", "",
           "Зміни кожної версії OrientPyx. Програма оновлюється сама — "
           "див. [Встановлення та оновлення](start/install.md).", ""]
    for release in releases:
        if release.get("draft") or release.get("prerelease"):
            continue
        version = (release.get("tag_name") or "").lstrip("v")
        date = (release.get("published_at") or "")[:10]
        if date:
            y, m, d = date.split("-")
            date = f"{d}.{m}.{y}"
        out += [f"## {version}" + (f" — {date}" if date else ""), ""]
        body = (release.get("body") or "").replace("\r\n", "\n")
        body = "\n".join(line for line in body.split("\n")
                         if not re.match(r"\s*(Co-Authored-By:|🤖 Generated with)", line))
        body = re.sub(r"(?m)^(#{1,5}) ", r"#\1 ", body)          # the release's own headings one level down
        body = re.sub(r"(?m)^#{3} Що нового\s*$\n?", "", body)   # «## Що нового» inside a release is redundant here
        out += [body.strip(), ""]
    _CHANGELOG = "\n".join(out) + "\n"
    return _CHANGELOG
