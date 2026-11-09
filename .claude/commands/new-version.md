---
description: Cut and publish a new OrientPyx release — bump version, commit changelog, tag, create the GitHub Release, and follow the GitHub Actions workflows that build, sign and upload the Velopack packages.
argument-hint: "(no args — you will be asked which segment to bump)"
---

Cut and publish a new OrientPyx release. Extra arguments (if any): **$ARGUMENTS**
The version bump is **always chosen by the user** via `AskUserQuestion` in Step 1.

You are releasing this app to end users, so treat this as an outward-facing action:
follow every step in order, verify as you go, and **stop and report** if any step fails
instead of pushing a half-finished release. Do not stop before the release workflows have
finished (Step 6) — that is where the packages are built, signed and uploaded.

## Step 0 — Preconditions (fail fast)

1. Confirm the working tree is on `main`: `git rev-parse --abbrev-ref HEAD`.
   If it is not `main`, stop and tell the user — releases are cut from `main`.
2. Confirm `gh` is authenticated — `gh auth status` (used for the release, its description
   and to watch the release workflows). The packages themselves are built by GitHub Actions,
   so `vpk` is not needed locally.
3. Do **not** require the working tree to be clean — the user's uncommitted work in
   progress *is* the content of this release. But do look at `git status` so you know
   what you are about to commit.

## Step 1 — Determine the version

- Read the current version from `src/OrientPyx.Presentation/OrientPyx.Presentation.csproj`
  (`<Version>`) and the latest tag: `git tag --sort=-v:refname | head -1`.
- **Always ask** the user which segment to bump — even if `$ARGUMENTS` contains a
  version. Call `AskUserQuestion` with exactly three options, computed from the base
  version (the latest of {csproj version, latest tag}), patch listed **first** and marked
  as the default:
  1. `Patch — <X.Y.Z+1> (Recommended)` — bump the 3rd segment (fixes/small changes).
  2. `Minor — <X.Y+1.0>` — bump the 2nd segment, reset patch to 0 (new features).
  3. `Major — <X+1.0.0>` — bump the 1st segment, reset minor and patch to 0.
  Show the concrete resulting version in each label (e.g. `0.5.2 → 0.5.3`). If the user
  picks "Other" and types an explicit version, use that (strip any leading `v`).
  Do not continue until the user has answered.
- **Validate** the new version:
  - It is a valid `MAJOR.MINOR.PATCH` semver.
  - It is strictly greater than the latest existing tag (Velopack requires this for
    clients to update). If not, stop and tell the user.
  - A tag `v<version>` does not already exist. If it does, stop.
- State the chosen version and why before continuing.

## Step 2 — Bump the version in code

- Edit `<Version>` in `src/OrientPyx.Presentation/OrientPyx.Presentation.csproj` to the
  new version. That is the single source of truth — the About page and the Velopack
  package both read it. (Search for the old version string elsewhere to be safe, but the
  csproj is normally the only place.)

## Step 2b — Update the user documentation (website/)

The documentation site (https://vadymkolesnyk.github.io/OrientPyx/, Ukrainian, for the people running competitions)
is published by `.github/workflows/docs.yml` on the tag this release pushes — so its texts must describe this release
before the commit. Screenshots, «Довідник екранів» (the in-app `Help.*` texts) and «Що нового» (the GitHub release
notes) regenerate by themselves; the hand-written pages do not.

1. List what may be stale: `python website/tools/affected.py` (changes since the previous tag, committed and not;
   pages per `website/docs-map.yml`, UI names renamed while the old name is still quoted, new UI strings, changed
   help texts, files no page claims).
2. For every listed page: read the diff of its trigger files and the page, and update the page so it matches the
   app — new options, renamed buttons, changed rules. A new feature with no home gets a section on the closest page
   (or a new page added to `website/mkdocs.yml` `nav` + an entry in `website/docs-map.yml`). Follow the writing
   rules in `website/README.md` (task-first, on-screen names in «лапках», menu paths with «→», «ви»). If a «?» help
   text (`Help.*` in both `uk-UA.json` and `en-US.json`) became wrong, fix it too.
3. A new screen or a new state worth showing → add a shot to `tests/OrientPyx.Tests/DocShots/DocShotTests*.cs`
   (helpers in `DocShooter.cs`; demo data in `DemoCompetitions.cs`) and reference it as `../shots/<name>.png`.
4. Render and build exactly as CI will: `scripts/docs.ps1 -Shots -Build` (renders the screenshots through the build
   lock, then `mkdocs build --strict`). Fix every warning; look at the screenshots of the pages you touched.
5. Nothing to change? Say so in the report and move on. Never skip step 4 when a page or a shot changed.

## Step 3 — Write the changelog / commit

- Gather what changed since the last release:
  `git log v<previous>..HEAD --oneline` **plus** the uncommitted changes in `git status`
  / `git diff --stat`. Read enough of the diff to describe the release honestly.
- Stage everything: `git add -A`.
- Create a commit on `main` whose message summarizes what changed since the previous
  version, in the user's usual concise style (a one-line subject like
  `Release v<version>: <headline>`, then a short bullet list of notable changes).
  End the commit message with:

  Keep bullets user-facing where possible (features/fixes), not mechanical file lists.

## Step 4 — Tag

- Create an annotated tag: `git tag -a v<version> -m "OrientPyx <version>"`.

## Step 5 — Push the branch and tag, then create the GitHub release (BEFORE the upload)

Push **before** building/uploading. This ordering matters: the release is attached to the
`v<version>` tag on GitHub, so the remote tip must already be the release commit:

```bash
git push origin main
git push origin v<version>
```

- If either push is rejected (e.g. the remote has commits you don't have), **stop** and
  report — do not force-push over someone else's work. A rejected *tag* push only happens
  if the tag already exists remotely, which at this point it should not.

Then **create the GitHub Release right away**, with the changelog from the commit body as
its notes, ending with the code-signing policy link (SignPath requires it on the download
page). Both release workflows started by the tag push merge into this release; without it each
`vpk upload` would create a draft of its own and the second one to publish fails with
`tag_name already_exists` — that is how 0.7.12 ended up with orphan drafts:

```bash
gh release create v<version> --verify-tag --title "OrientPyx <version>" --notes "<markdown changelog>

[Code signing policy](https://github.com/VadymKolesnyk/OrientPyx#code-signing-policy)"
```

`release-windows.yml` also creates it if missing (bare notes), but creating it here gives it the
changelog from the start.

## Step 6 — Watch the release workflows

The tag push (Step 5) started both package builds on GitHub Actions; nothing is built locally:

- **`Release Windows`** (`.github/workflows/release-windows.yml`): downloads the previous
  Windows release (for the delta), runs `build/publish.ps1` (app + standard plugins),
  **code-signs through SignPath** when signing is configured (`docs/code-signing.md`), checks
  the signatures and uploads the installer, full/delta packages and `releases.win.json` with
  `vpk upload … --merge`.
- **`Release macOS`** (`.github/workflows/release-macos.yml`): waits for the published release
  (up to 20 min), then builds `osx-arm64` / `osx-x64` and merges them into the same release.

Find and follow the runs:

```bash
gh run list --workflow release-windows.yml --limit 1
gh run list --workflow release-macos.yml --limit 1
gh run watch <windows-run-id>
```

- **Signing approvals.** If the Windows run reaches «Sign the package files (SignPath request 1 of 2)»
  and waits, signing uses the `release-signing` policy: tell the user right away to approve the
  request in SignPath (the step log prints its URL; the Approver also gets an e-mail), and again
  for «Sign the installer (request 2 of 2)». Each waits up to an hour. With `test-signing` or no
  signing configured nothing waits.
- Once both are done, `gh release view v<version> --json assets -q '.assets[].name'` should list
  `OrientPyx-win-Setup.exe`, the `OrientPyx-<version>-full.nupkg` (+ delta) and `releases.win.json`
  next to `OrientPyx-osx-arm64-Setup.pkg` and `OrientPyx-osx-x64-Setup.pkg`.
- If a workflow failed, report it (`gh run view <id> --log-failed`). Re-run it with
  `gh run rerun <id> --failed` (e.g. an approval that timed out), or after a fix on `main` with
  `gh workflow run release-windows.yml --ref main -f version=<version> -f upload=true` (same for
  `release-macos.yml`). The packages of every run are also kept as its `orientpyx-windows-<version>` /
  `orientpyx-macos-<version>` artifact.
- **Last resort only, and only after asking the user:** if the Windows workflow cannot run at all,
  `build/publish.ps1 -Version <version> -Upload` (with `$env:GITHUB_TOKEN = (gh auth token)`) builds and
  uploads an **unsigned** Windows release from this machine. Never while the workflow is still
  running or after it uploaded — the two uploads would collide.
- If a run says `tag_name already_exists`, look for a stray draft:
  `gh api repos/VadymKolesnyk/OrientPyx/releases -q '.[] | select(.tag_name=="v<version>") | "\(.id) draft=\(.draft)"'`
  — its packages are also in the run's `orientpyx-macos-<version>` / `orientpyx-windows-<version>` artifact,
  which can be re-uploaded with `vpk upload github --outputDir <artifact dir> --channel <channel> … --merge`
  after deleting the draft (`gh api -X DELETE repos/VadymKolesnyk/OrientPyx/releases/<id>`).
- Verify the remote tag points at the release commit — the annotated tag dereferences with `^{}`:
  `git ls-remote origin 'refs/tags/v<version>^{}'` should equal `git rev-parse HEAD`.
  If it does **not** match, tell the user the tag is off and ask before force-updating it.
- If a workflow keeps failing, **stop** and report the error. The branch/tag are already pushed,
  but do not hand-fake the release assets — re-run the workflow once the cause is fixed.

## Step 7 — Improve the release notes

Step 5 created the release with the commit-body changelog. Re-read it now that the assets are
in and replace it with the final, human-readable changelog so users understand what changed:

```bash
gh release edit v<version> --notes "<markdown changelog>"
```

Write the notes as a short markdown list of the notable user-facing changes in this
release (the same content as the commit body, expanded a little). Ukrainian is fine for
user-facing wording if that matches how the app speaks to its users, but a bilingual or
English summary is also acceptable — match what past releases used. Keep the closing
`[Code signing policy](https://github.com/VadymKolesnyk/OrientPyx#code-signing-policy)` line.

The tag push also started the **Docs** workflow (`.github/workflows/docs.yml`: screenshots from the tagged app,
`mkdocs build --strict`, GitHub Pages). Its «Що нового» page reads the release notes while it builds, which may have
been before this step — once the notes are final, republish: `gh workflow run docs.yml --ref main`. If the Docs run
failed, `gh run view <id> --log-failed`; it never blocks the packages.

## Step 8 — Report

Summarize for the user:
- the released version and the previous one,
- the release URL (`gh release view v<version> --json url -q .url`),
- whether the Windows and macOS packages landed (or that a workflow is still running / failed),
  and whether the Windows package is signed (and with which policy) or unsigned,
- the key changes shipped, and which documentation pages were updated (Step 2b) and whether the Docs workflow
  published the site,
- and remind them that installed clients will pick up the update via
  **Settings → Version & updates**.
