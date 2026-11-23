---
paths:
  - "website/**"
  - "**/DocShots/**"
  - "**/DocsLinks*"
  - "src/OrientPyx.Plugins.Sdk/**"
---

## Documentation site (`website/`)

User documentation, **Ukrainian only**, for the people running competitions — not for developers: task-first pages,
on-screen names and menu paths («Старт → Жеребкування»), no class names. Writing rules: `website/README.md`. The one
developer section is «Власні плагіни» (`website/content/plugins/`, the plugin API guide; authors get the SDK by cloning
this repo and adding a project under `plugins/`). MkDocs Material, versions pinned in `website/requirements.txt` (keep
`mkdocs` 1.x). `.github/workflows/docs.yml` publishes to GitHub Pages (`https://vadymkolesnyk.github.io/OrientPyx/`)
on every `v*` tag (texts + screenshots of the release); a manual run republishes the branch's texts with the latest
release's screenshots.
- **Screenshots and demo competitions are generated, never committed** (`website/content/shots|demo/`, git-ignored):
  the `DocShots` tests (`tests/OrientPyx.Tests/DocShots/`, `[DocShotFact]`, skipped unless `ORIENTPYX_DOCSHOTS` is set)
  drive the headless app over the demo competitions of `DemoCompetitions.cs` (fictional people, seeded, one per format
  + a two-day cup) and save `shots/<name>.png` + `manifest.json`; `DocShooter` crops to a control, frames controls,
  draws open popups/flyouts in place. Never put a real path or machine name on a shot (mask it like the finish page's
  read-out file). `scripts/docs.ps1` renders them when missing (`-Shots` = again) then previews; `-Build` = the strict
  CI build.
- **Generated pages** (`website/hooks.py`): «Довідник екранів» — one page per `Help.<Screen>` (sections per
  `Help.<Screen>.<Block>`) straight from `uk-UA.json`; «Що нового» from the GitHub releases. The «?» dialog's
  «Детальніше в документації» and «Довідка → Документація» open them through `Presentation/Services/DocsLinks.cs` —
  its kebab rule must stay the hook's (`DocsLinksTests`). The hook gives each release heading the id `v<version>`:
  the launch after an update (Velopack `OnRestarted` → `Program.UpdatedToVersion`) shows «OrientPyx оновлено»
  (`AppUpdatedViewModel`) linking `DocsLinks.Changelog(version)` = `changelog/#v<version>`.
- **Keeping it current**: `/new-version` Step 2b — `python website/tools/affected.py` lists the pages whose code changed
  since the last tag (`website/docs-map.yml`), UI names renamed while still quoted in the docs, new strings and changed
  help; update the pages, add shots, `scripts/docs.ps1 -Shots -Build`. A new page → `mkdocs.yml` nav + `docs-map.yml`.
- The in-app «?» texts (`Help.*`) are the short form of the same content: `LocalizationTests.EveryHelpButtonHasItsText`
  / `EveryHelpTextHasAButton` keep every «?» backed by uk + en text and every help text reachable (a page whose
  view-model name differs from its help prefix overrides `HelpKeyPrefix`). `PluginDocsTests` keeps every public SDK
  type in `website/content/plugins/api.md`.
