# Packaging & updates (Velopack)

OrientPyx ships as a **Velopack** app: `publish.ps1` builds a Windows installer (`Setup.exe`) and an
update package, and the running app checks GitHub Releases for newer versions.

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
