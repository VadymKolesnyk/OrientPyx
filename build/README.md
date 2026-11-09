# Packaging & updates (Velopack)

OrientPyx ships as a **Velopack** app: `publish.ps1` builds a Windows installer (`Setup.exe`) and an
update package, `publish-mac.sh` builds the macOS `.pkg` / `.app` — both run by GitHub Actions on the release
tag — and the running app checks GitHub Releases for newer versions. One GitHub Release holds every
platform; each platform reads its own Velopack **channel**: `win`, `osx-arm64` (Apple Silicon), `osx-x64`
(Intel).

## One-time setup

```powershell
dotnet tool install -g vpk        # the Velopack CLI (only for local builds; CI installs its own)
```

## Cut a release

1. Bump `<Version>` in `src/OrientPyx.Presentation/OrientPyx.Presentation.csproj`, commit, tag `v<version>`,
   push both, and create the GitHub Release `v<version>` with the changelog (`/new-version` does all of it).
2. The tag push starts two workflows; both merge into that one release (`--merge`):
   - **`Release Windows`** (`.github/workflows/release-windows.yml`, `windows-latest`): creates the release if it
     is missing, downloads the previous Windows release into `build/releases` (`vpk download github`, for the
     delta), runs `build/publish.ps1`, **code-signs** through SignPath when configured (see below), checks the
     signatures and runs `vpk upload github`. Packages are kept as the run's `orientpyx-windows-<version>` artifact.
   - **`Release macOS`** (`.github/workflows/release-macos.yml`, `macos-latest`): waits for the published release
     (up to 20 min; creates it only if nobody did), then `build/publish-mac.sh` publishes `osx-arm64` and
     `osx-x64`, packs each and uploads.

   Both can also be started by hand from the Actions tab (inputs: version, upload).

   One published release must exist before any upload: a `vpk upload` that finds none creates a draft of its
   own, and when two drafts share one tag the first to publish wins and the other fails with
   `tag_name already_exists` (0.7.12).

### Building locally

```powershell
./build/publish.ps1 -Version 1.1.0
```

Output lands in `build/releases/`:
- `OrientPyx-win-Setup.exe` — the installer to hand to users for a **first install**.
- `*-full.nupkg` (+ `*-delta.nupkg` from the 2nd release on) — the **update** feed.

The local package is **unsigned**. `-Upload` (needs `$env:GITHUB_TOKEN` with `repo` scope and the pushed tag)
still publishes it — a fallback only for when the Windows workflow cannot run; never for a tag the workflow
already builds, or the two uploads collide.

## Windows code signing (SignPath)

Free certificate from the SignPath Foundation open-source programme; the full how-to (application, SignPath and
GitHub setup, test run, troubleshooting) is [`docs/code-signing.md`](../docs/code-signing.md). In short:

- SignPath signs only artifacts uploaded by a GitHub Actions job that ran on GitHub-hosted runners — so a signed
  Windows release can come only from `release-windows.yml`, never from a local build.
- Switched on by the secret `SIGNPATH_API_TOKEN` + the variable `SIGNPATH_ORGANIZATION_ID` (optional:
  `SIGNPATH_PROJECT_SLUG`, `SIGNPATH_SIGNING_POLICY_SLUG` = `test-signing` → `release-signing`,
  `SIGNPATH_ARTIFACT_CONFIGURATION_SLUG`). Without them the workflow builds unsigned, as before.
- Velopack has to sign during `vpk pack`, so the workflow packs twice from one build: a throwaway pack whose
  `--signTemplate` hook (`signpath/vpk-sign-hook.ps1 -Mode Collect`) collects the files to sign → **request 1** →
  the real pack swaps the signed copies in (`-Mode Swap`, matched by SHA-256). `OrientPyx-win-Setup.exe` is built
  last and is not in the feed's hashes → **request 2** after packing. Then `signpath/Test-ReleaseSignatures.ps1`.
  With `release-signing` each request waits (up to an hour) for an Approver in SignPath.
- Only the project's own binaries are signed (`signpath/SigningRules.ps1`, mirrored in
  `signpath/artifact-configuration.xml`): `OrientPyx.exe`, `OrientPyx*.dll`, the standard plugins, Velopack's
  launcher stub and `Update.exe` (`Squirrel.exe`), and `Setup.exe`. They must carry product name `OrientPyx` and
  product version = the release (`src/Directory.Build.props`, `plugins/Directory.Build.props`, plugin build with
  `-p:InformationalVersion`).
- `vpk` is pinned in the workflow (the hook depends on how it calls the template) — re-check the hook before bumping.

## macOS specifics

- Velopack can only build a `.app`/`.pkg` **on macOS** (`vpk` on Windows offers just `bundle` for OSX), hence
  the CI workflow. To build locally on a Mac: `dotnet tool install -g vpk`, then
  `build/publish-mac.sh 1.1.0 [--upload] [--runtime osx-arm64|osx-x64|all]`.
- Output per architecture in `build/releases-mac/<rid>/`: `OrientPyx-<rid>-Setup.pkg` (installer),
  `OrientPyx-<rid>-Portable.zip` (the bare `.app`), `*-full.nupkg`/`*-delta.nupkg` and `releases.<rid>.json`.
- The `.icns` icon is generated on the fly from `Assets/orientpyx.png` with `sips` + `iconutil`.
- **Signing & notarization** are optional and driven by repository secrets (listed at the top of the
  workflow file): a `Developer ID Application` certificate (`.p12`, base64) with its password, the two identity
  names, and `APPLE_ID` / `APPLE_TEAM_ID` / `APPLE_APP_PASSWORD` for `notarytool`. With them `vpk` signs the
  `.app`, signs the `.pkg` (if a `Developer ID Installer` identity is given), notarizes and staples. Without
  them the packages are **unsigned**: Gatekeeper blocks the first launch and the user must right-click →
  «Open» (or System Settings → Privacy & Security → «Open Anyway»). Auto-updates work either way.
- Installed to `/Applications/OrientPyx.app`; data lives in `~/Library/Application Support/OrientPyx/data-root`
  (the `.app` is replaced wholesale by every update — see `Program.cs`, `IsVelopackInstallLayout`, which
  recognises the bundled `UpdateMac` beside the executable).
- Not available on macOS: printing to a thermal / system printer (GDI, `System.Drawing`) — the split,
  winners and statement printouts report «unsupported»; Word/HTML export is unaffected.
- The `.opyx` association comes from `build/mac/Info.plist` (`CFBundleDocumentTypes` + an exported UTI),
  which `publish-mac.sh` fills with the version and passes to `vpk pack --plist`. Velopack copies a provided
  plist **as-is**, so that template must keep every standard key (name, executable, identifier, version,
  icon) in sync with what Velopack would generate. Finder delivers the file as an Avalonia file activation
  (`IActivatableLifetime`, handled in `App.axaml.cs`), not as a command-line argument.

> **Deltas need history.** `vpk` builds delta packages by diffing against the previous packages already
> in `build/releases/`. That folder is git-ignored, so before packing a new version, restore the prior
> release's `*.nupkg` there (e.g. `vpk download github ...`, as `release-windows.yml` does) — otherwise you
> only get a full package.

## How updating works at runtime

- Installed to `%LocalAppData%\OrientPyx`; each version lives in its own `current`/`app-x.y.z` folder,
  so **competition data must not** sit next to the exe (`OrientPyx.Presentation.exe`) — the app redirects it to
  `%LocalAppData%\OrientPyx\data-root` when it detects an installed build (see `Program.cs`).
- On **Settings → Version & updates**, the user can check for and install updates; the app downloads only
  the delta and restarts into the new version.
- A dev build (`dotnet run`) is *not* installed: the update controls are hidden and data stays next to the
  build output, exactly as before.
```
