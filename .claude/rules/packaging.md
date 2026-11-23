---
paths:
  - "build/**"
  - ".github/**"
  - "**/Program.cs"
  - "**/App.axaml.cs"
  - "**/AppDatabasePaths*"
  - "**/FileAssociation*"
  - "docs/code-signing.md"
---

## Platforms & packaging

Windows and macOS are shipped; Linux runs from source only. On the `v<version>` tag GitHub Actions runs
`build/publish.ps1` (Windows, `.github/workflows/release-windows.yml`) and `build/publish-mac.sh` (macOS,
`release-macos.yml`); both pack with Velopack into one GitHub Release, each platform on its own channel (`win`,
`osx-arm64`, `osx-x64`; both uploads use `--merge`). The release is created first — by `/new-version` (or the
Windows workflow) with `gh release create`, the macOS workflow waits for it — so neither `vpk upload` ever creates
a draft of its own (two drafts for one tag = the second publish fails with `tag_name already_exists`). A local
`publish.ps1` builds the same package **unsigned** (`-Upload` = emergency fallback only). **Windows code signing**
goes through the SignPath Foundation (free OSS certificate, publisher «SignPath Foundation»), which signs only
artifacts of a GitHub-hosted Actions run: the workflow packs twice (`build/signpath/vpk-sign-hook.ps1`
collects the files `vpk` would sign, request 1; the real pack swaps the signed copies in) and signs `Setup.exe`
after packing (request 2); on for `secrets.SIGNPATH_API_TOKEN` + `vars.SIGNPATH_ORGANIZATION_ID`, unsigned
otherwise. Signed files must say product `OrientPyx` and product version = the release — keep
`src/Directory.Build.props` / `plugins/Directory.Build.props` on every project and list a new own binary in
`build/signpath/SigningRules.ps1` + `artifact-configuration.xml`. How-to and status: `docs/code-signing.md`.
Details and the optional Apple signing secrets: `build/README.md`. Keep the code portable:
anything Windows-only (GDI printing in `DataAccess/Printing`, `FileAssociation`, Explorer) must be guarded by
`OperatingSystem.IsWindows()` and degrade gracefully (the print services expose `IsSupported`); reveal/open
files through `Presentation/Services/FileReveal.cs`, never by launching `explorer.exe` directly. On macOS the
installed layout is the `.app` bundle with `UpdateMac` beside the exe (`Program.IsVelopackInstallLayout`) and
data goes to `~/Library/Application Support/OrientPyx/data-root`. The `.opyx` association on macOS is the
bundle's `build/mac/Info.plist` (copied as-is by `vpk pack --plist`, so keep its standard keys complete) and the
file arrives as an `IActivatableLifetime` file activation handled in `App.axaml.cs`, not in `args`.
