---
description: Cut and publish a new OrientPyx release — bump version, commit changelog, tag, build the Velopack package, and upload to GitHub Releases.
argument-hint: "[version]  (optional, e.g. 0.2.0 — defaults to patch-bumping the latest tag)"
---

Cut and publish a new OrientPyx release. The requested version is: **$ARGUMENTS**
(if empty, patch-bump the latest release — e.g. 0.1.5 → 0.1.6).

You are releasing this app to end users, so treat this as an outward-facing action:
follow every step in order, verify as you go, and **stop and report** if any step fails
instead of pushing a half-finished release. Do not skip the build.

## Step 0 — Preconditions (fail fast)

1. Confirm the working tree is on `main`: `git rev-parse --abbrev-ref HEAD`.
   If it is not `main`, stop and tell the user — releases are cut from `main`.
2. Confirm required tooling is present:
   - `vpk` — `which vpk` (Velopack CLI; if missing, tell the user to run
     `dotnet tool install -g vpk`).
   - `gh` is authenticated — `gh auth status` (used both for the release description
     and to supply the upload token).
3. Do **not** require the working tree to be clean — the user's uncommitted work in
   progress *is* the content of this release. But do look at `git status` so you know
   what you are about to commit.

## Step 1 — Determine the version

- Read the current version from `src/OrientPyx.Presentation/OrientPyx.Presentation.csproj`
  (`<Version>`) and the latest tag: `git tag --sort=-v:refname | head -1`.
- If the user passed a version in `$ARGUMENTS`, use it (strip any leading `v`).
- Otherwise, patch-bump the **latest** of {csproj version, latest tag} — increment the
  third (patch) segment by 1. E.g. `0.1.5` → `0.1.6`.
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

## Step 5 — Build + upload the Velopack release

`build/releases/` already holds the previous `.nupkg` packages, so Velopack can build a
delta. Run the existing publish script, which builds, packs, and uploads in one shot:

```powershell
$env:GITHUB_TOKEN = (gh auth token)
./build/publish.ps1 -Version <version> -Upload
```

- Run it with the Bash tool via `powershell -NoProfile -File build/publish.ps1 ...`
  **or** the PowerShell tool — either is fine; set `GITHUB_TOKEN` from `gh auth token`
  first so the script's GitHub upload works without asking for a token.
- This publishes a GitHub Release tagged `v<version>` named `OrientPyx <version>` with
  the installer, full/delta packages, and the `RELEASES`/`releases.win.json` update feed.
- If the script fails, **stop** and report the error. Do not push the branch/tag pointing
  at a release that was never built, and do not hand-fake the assets.

## Step 6 — Push the branch and tag

Only after the publish succeeded:

```bash
git push origin main
git push origin v<version>
```

(`vpk upload` already created the GitHub Release; pushing the tag just makes the local and
remote tags agree. If the tag already exists remotely from the upload, that push is a
no-op — that's fine.)

## Step 7 — Improve the release notes

The `vpk`-generated release body is minimal. Replace it with a clear, human-readable
changelog so users understand what changed:

```bash
gh release edit v<version> --notes "<markdown changelog>"
```

Write the notes as a short markdown list of the notable user-facing changes in this
release (the same content as the commit body, expanded a little). Ukrainian is fine for
user-facing wording if that matches how the app speaks to its users, but a bilingual or
English summary is also acceptable — match what past releases used.

## Step 8 — Report

Summarize for the user:
- the released version and the previous one,
- the release URL (`gh release view v<version> --json url -q .url`),
- the key changes shipped,
- and remind them that installed clients will pick up the update via
  **Settings → Version & updates**.
