<#
.SYNOPSIS
    Builds a Velopack installer + update package for OrientPyx and (optionally) uploads it to GitHub Releases.

.DESCRIPTION
    Produces a Windows installer (Setup.exe), a full package and — from the second release on — delta
    packages, under build/releases. The same folder is what clients read to auto-update. First run creates
    the initial release; subsequent runs compare against the previous packages there and emit deltas, so
    keep build/releases (or re-download it with `vpk download github`) before packing a new version.

    Releases are built by the GitHub Actions workflow .github/workflows/release-windows.yml on the
    v<Version> tag push — that is the only build SignPath can code-sign (docs/code-signing.md). The workflow
    calls this script (with -OutputDir / -SkipBuild / -SignTemplate / -NoDelta); run locally it builds the
    same, unsigned package for testing. -Upload from a local run is a fallback for when the workflow cannot
    run: it publishes an UNSIGNED Windows release and must not race the workflow for the same tag.

    macOS packages are NOT built here (Velopack can only pack a .app/.pkg on macOS): they come from
    build/publish-mac.sh, run by .github/workflows/release-macos.yml on the same tag. All uploads merge into
    the same GitHub Release (--merge), each under its own Velopack channel (win / osx-arm64 / osx-x64).

    Requires the Velopack CLI (vpk). Install once with:  dotnet tool install -g vpk

.PARAMETER Version
    Semantic version of this release, e.g. 1.1.0. Must be higher than the previous release for clients to update.

.PARAMETER Upload
    If set, uploads the release to GitHub Releases (repo VadymKolesnyk/OrientPyx). Needs a token in
    $env:GITHUB_TOKEN with 'repo' scope.

.PARAMETER OutputDir
    Where vpk writes the packages (default build/releases). The signed CI build packs once into a throwaway
    folder to collect the files to sign.

.PARAMETER SkipBuild
    Reuse build/publish as it is (no dotnet publish / plugin build). The signed CI build packs twice from the
    same bytes: the second pack must see exactly the files the first one collected.

.PARAMETER SignTemplate
    Passed to `vpk pack --signTemplate` (with a large --signParallel, so a template using {{file...}} gets every
    file of a phase in one call). See build/signpath/vpk-sign-hook.ps1. Needs PowerShell 7.3+ when the template
    contains quotes (Windows PowerShell 5.1 mangles embedded quotes in native arguments).

.PARAMETER NoDelta
    Skip delta generation (--delta None) — for the throwaway collecting pack.

.EXAMPLE
    ./build/publish.ps1 -Version 1.1.0
    ./build/publish.ps1 -Version 1.2.0 -Upload
#>
param(
    [Parameter(Mandatory = $true)] [string] $Version,
    [switch] $Upload,
    [string] $OutputDir,
    [switch] $SkipBuild,
    [string] $SignTemplate,
    [switch] $NoDelta
)

$ErrorActionPreference = 'Stop'

$repoRoot   = Split-Path -Parent $PSScriptRoot
$project    = Join-Path $repoRoot 'src/OrientPyx.Presentation/OrientPyx.Presentation.csproj'
$publishDir = Join-Path $repoRoot 'build/publish'
$releaseDir = if ($OutputDir) { [System.IO.Path]::GetFullPath($OutputDir) } else { Join-Path $repoRoot 'build/releases' }
$runtime    = 'win-x64'
$packId     = 'OrientPyx'
$packTitle  = 'OrientPyx'
$iconPath   = Join-Path $repoRoot 'src/OrientPyx.Presentation/Assets/orientpyx.ico'

# --- 0. Ensure the vpk tool is available ----------------------------------------------------------
if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
    throw "Velopack CLI (vpk) not found. Install it with:  dotnet tool install -g vpk"
}
if ($SignTemplate -and $SignTemplate.Contains('"') -and $PSVersionTable.PSVersion -lt [version]'7.3') {
    throw "A -SignTemplate with quotes needs PowerShell 7.3+ (pwsh); this is $($PSVersionTable.PSVersion)."
}

if ($SkipBuild) {
    if (-not (Test-Path (Join-Path $publishDir 'OrientPyx.exe'))) { throw "-SkipBuild: $publishDir holds no build." }
    Write-Host "Reusing the build in $publishDir" -ForegroundColor Cyan
} else {
    # --- 1. Clean publish output ------------------------------------------------------------------
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
    New-Item -ItemType Directory -Path $publishDir | Out-Null

    # --- 2. Publish the app (self-contained: end users need no .NET installed) ---------------------
    Write-Host "Publishing $packId $Version ($runtime)..." -ForegroundColor Cyan
    dotnet publish $project `
        -c Release `
        -r $runtime `
        --self-contained true `
        -p:Version=$Version `
        -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

    # --- 2b. Build the standard plugins and ship them in <app dir>\plugins -------------------------
    # No -p:Version here: the plugins keep their own versions, and the SDK keeps its explicit AssemblyVersion.
    # Only the product version follows the app (InformationalVersion), as code signing requires one product
    # version per release (docs/code-signing.md).
    Write-Host "Building plugins..." -ForegroundColor Cyan
    $pluginsSln = Join-Path $repoRoot 'plugins/OrientPyx.Plugins.sln'
    $pluginsOut = Join-Path $repoRoot 'artifacts/plugins'
    if (Test-Path $pluginsOut) { Remove-Item $pluginsOut -Recurse -Force }
    dotnet build $pluginsSln -c Release -p:InformationalVersion=$Version
    if ($LASTEXITCODE -ne 0) { throw "Plugin build failed." }
    $publishPlugins = Join-Path $publishDir 'plugins'
    New-Item -ItemType Directory -Path $publishPlugins -Force | Out-Null
    Get-ChildItem $pluginsOut -Directory | ForEach-Object {
        Copy-Item $_.FullName -Destination $publishPlugins -Recurse -Force
    }
}

# --- 3. Pack with Velopack (installer + update package + deltas) -----------------------------------
Write-Host "Packing Velopack release $Version into $releaseDir..." -ForegroundColor Cyan
$packArgs = @(
    'pack',
    '--packId', $packId,
    '--packVersion', $Version,
    '--packDir', $publishDir,
    '--mainExe', 'OrientPyx.exe',
    '--packTitle', $packTitle,
    '--icon', $iconPath,
    '--outputDir', $releaseDir
)
if ($SignTemplate) { $packArgs += @('--signTemplate', $SignTemplate, '--signParallel', '1000') }
if ($NoDelta) { $packArgs += @('--delta', 'None') }
vpk @packArgs
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed." }

Write-Host "Release ready in: $releaseDir" -ForegroundColor Green

# --- 4. (optional) Upload to GitHub Releases ------------------------------------------------------
if ($Upload) {
    if (-not $env:GITHUB_TOKEN) { throw "Set `$env:GITHUB_TOKEN (repo scope) to upload." }

    # Create the GitHub Release up front (published, empty) so every upload - this one and the macOS
    # workflow's two channels - merges into ONE existing release. A `vpk upload` that finds no release
    # creates a draft of its own; with two uploads racing, the first to publish wins and the other fails
    # at publish with "tag_name already_exists", leaving its packages in an orphan draft (0.7.12). The
    # macOS workflow waits for this release before uploading. Needs the v<Version> tag pushed already.
    if (Get-Command gh -ErrorAction SilentlyContinue) {
        gh release view "v$Version" --json id *> $null
        if ($LASTEXITCODE -ne 0) {
            Write-Host "Creating GitHub release v$Version..." -ForegroundColor Cyan
            gh release create "v$Version" --verify-tag --title "OrientPyx $Version" --notes "OrientPyx $Version"
            if ($LASTEXITCODE -ne 0) { throw "gh release create failed (is the v$Version tag pushed?)." }
        }
    } else {
        Write-Warning "gh CLI not found: vpk will create the release itself, which races with the macOS workflow."
    }

    Write-Host "Uploading release $Version to GitHub..." -ForegroundColor Cyan
    vpk upload github `
        --outputDir $releaseDir `
        --repoUrl 'https://github.com/VadymKolesnyk/OrientPyx' `
        --token $env:GITHUB_TOKEN `
        --publish `
        --merge `
        --releaseName "OrientPyx $Version" `
        --tag "v$Version"
    if ($LASTEXITCODE -ne 0) { throw "vpk upload failed." }
    Write-Host "Uploaded." -ForegroundColor Green
}
