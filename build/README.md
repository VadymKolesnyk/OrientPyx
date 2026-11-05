# Packaging & updates (Velopack)

OrientPyx ships as a **Velopack** app: `publish.ps1` builds a Windows installer (`Setup.exe`) and an
update package, `publish-mac.sh` (run by GitHub Actions on a macOS runner) builds the macOS `.pkg` /
`.app`, and the running app checks GitHub Releases for newer versions. One GitHub Release holds every
platform; each platform reads its own Velopack **channel**: `win`, `osx-arm64` (Apple Silicon), `osx-x64`
(Intel).

## One-time setup

```powershell
dotnet tool install -g vpk        # the Velopack CLI
```

## Cut a release

1. Bump `<Version>` in `src/OrientPyx.Presentation/OrientPyx.Presentation.csproj` (or just pass `-Version`).
2. Build the package:

   ```powershell
   ./build/publish.ps1 -Version 1.1.0
   ```

   Output lands in `build/releases/`:
   - `OrientPyx-win-Setup.exe` — the installer to hand to users for a **first install**.
   - `*-full.nupkg` (+ `*-delta.nupkg` from the 2nd release on) — the **update** feed.

3. Publish so installed apps can update — either let the script upload to GitHub Releases:

   ```powershell
   $env:GITHUB_TOKEN = '...'          # a token with 'repo' scope
   ./build/publish.ps1 -Version 1.1.0 -Upload
   ```

   …or attach the whole `build/releases/` contents to a GitHub Release manually.

   With `-Upload` the script first **creates the GitHub Release** `v<version>` (via `gh`, so push the tag
   first) and then merges the packages into it. Creating it up front is what keeps the two uploads (this
   one and the macOS workflow's) from each making a draft of their own: when two drafts share one tag the
   first to publish wins and the other fails with `tag_name already_exists`.

4. **macOS** is built by the `Release macOS` workflow (`.github/workflows/release-macos.yml`): pushing the
   `v<version>` tag starts it on a `macos-latest` runner. It first waits for the published release
   `v<version>` to exist (up to 20 min; creates it only if nobody did), then `build/publish-mac.sh`
   publishes `osx-arm64` and `osx-x64`, packs each with `vpk` and uploads into that release with `--merge`
   (the Windows upload also passes `--merge`, so the order of the two uploads does not matter). It can also
   be started by hand from the Actions tab (inputs: version, upload). The packages are kept as a workflow
   artifact too.

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
> release's `*.nupkg` there (e.g. `vpk download github ...`) — otherwise you only get a full package.

## How updating works at runtime

- Installed to `%LocalAppData%\OrientPyx`; each version lives in its own `current`/`app-x.y.z` folder,
  so **competition data must not** sit next to the exe (`OrientPyx.Presentation.exe`) — the app redirects it to
  `%LocalAppData%\OrientPyx\data-root` when it detects an installed build (see `Program.cs`).
- On **Settings → Version & updates**, the user can check for and install updates; the app downloads only
  the delta and restarts into the new version.
- A dev build (`dotnet run`) is *not* installed: the update controls are hidden and data stays next to the
  build output, exactly as before.
```
