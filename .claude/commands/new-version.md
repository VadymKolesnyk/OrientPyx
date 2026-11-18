---
description: Cut and publish a new OrientPyx release — bump version, commit changelog, tag, create the GitHub Release, and follow the GitHub Actions workflows that build, sign and upload the Velopack packages.
argument-hint: "(no args — you will be asked which segment to bump; patch if no answer in 5 min)"
---

Cut and publish a new OrientPyx release. Extra arguments (if any): **$ARGUMENTS**
The version bump is **chosen by the user** in Step 1; with no answer within **5 minutes**
the recommended patch bump is taken.

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
  version — but **never wait longer than 5 minutes**: no answer by then = the recommended
  patch bump. `AskUserQuestion` has no timeout, so do **not** use it for this question:
  1. Compute three options from the base version (the latest of {csproj version, latest tag}),
     each with the concrete resulting version (e.g. `0.5.2 → 0.5.3`):
     1. `Patch — <X.Y.Z+1>` (**recommended**) — bump the 3rd segment (fixes/small changes).
     2. `Minor — <X.Y+1.0>` — bump the 2nd segment, reset patch to 0 (new features).
     3. `Major — <X+1.0.0>` — bump the 1st segment, reset minor and patch to 0.
  2. Start a 5-minute timer in the background — Bash `sleep 300` with
     `run_in_background: true` — and note its task id.
  3. Show the user the three options as a short numbered list, say that without an answer in
     5 minutes the patch version is released, and that they can reply `1`/`2`/`3`,
     `patch`/`minor`/`major` or an explicit version. Then **end the turn** — do nothing else
     (no commits, no edits, no further steps) while waiting.
  4. Whichever comes first decides:
     - **The user answers** → stop the timer (`TaskStop` with its id) and use their choice;
       an explicit version is used as typed (strip any leading `v`). An answer that names no
       version (a question, «стоп», something unclear) → do not release: answer it / ask
       again with `AskUserQuestion` — the user is here, no timer this time.
     - **The timer's notification arrives** and the user has not answered → take the patch
       version, say so plainly («Відповіді не було 5 хв — випускаю patch <version>») and go on.
     - A timer notification arriving after the version was already chosen is ignored.
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
- **Write the final release notes now**, before the tag is pushed — a markdown file in the scratchpad (passed to
  `gh release create --notes-file` in Step 5). They depend only on what changed, never on the build, so there is
  nothing to wait for: the Docs workflow started by the tag push builds «Що нового» from them, and notes finished
  later would need a second Docs run. Format: `## Що нового`, then a short markdown list of the notable user-facing
  changes — the commit body expanded a little, a **bold lead** per item (match past releases:
  `gh release view v<previous> --json body -q .body`). Plain GitHub markdown — no MkDocs syntax such as `++Ctrl+Z++`.
  Call out anything users must act on (e.g. a `NetworkProtocol.Version` bump = host and stations update together).
  End with the line
  `[Code signing policy](https://github.com/VadymKolesnyk/OrientPyx#code-signing-policy)` (SignPath requires it
  on the download page).

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

Then **create the GitHub Release right away** — the very next command after the tag push — with the final notes
written in Step 3. Both release workflows started by the tag push merge into this release; without it each
`vpk upload` would create a draft of its own and the second one to publish fails with
`tag_name already_exists` — that is how 0.7.12 ended up with orphan drafts:

```bash
gh release create v<version> --verify-tag --title "OrientPyx <version>" --notes-file <scratchpad>/release-notes.md
```

`release-windows.yml` also creates it if missing (bare notes), but creating it here gives it the
final notes from the start. The tag push also started the **Docs** workflow, which reads the release notes only at
its `mkdocs build` step — after rendering the screenshots, minutes later — so a release created right after the push
is in time and that one run publishes «Що нового» with the final notes.

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

## Step 7 — Check the release notes and the Docs run

The notes are already final (Step 3 → Step 5) — do **not** rewrite them for style. Check
`gh release view v<version> --json body -q .body` is what you wrote and still true after Step 6.

- Edit them (`gh release edit v<version> --notes-file …`) only for a real reason — a fact that changed (a package
  that did not land, a known problem found while watching the runs). Only after such an edit, and only once the
  Docs run of the tag push has finished, republish the site so «Що нового» follows:
  `gh workflow run docs.yml --ref main`.
- Wait for the **Docs** run of the tag push (`.github/workflows/docs.yml`: screenshots from the tagged app,
  `mkdocs build --strict`, GitHub Pages) to finish. If it failed, `gh run view <id> --log-failed`; it never blocks
  the packages.

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
