<#
.SYNOPSIS
    Builds a Velopack installer + update package for OrientPyx and (optionally) uploads it to GitHub Releases.

.DESCRIPTION
    Produces a Windows installer (Setup.exe), a full package and — from the second release on — delta
    packages, under build/releases. The same folder is what clients read to auto-update. First run creates
    the initial release; subsequent runs compare against the previous packages there and emit deltas, so
    keep build/releases (or re-download it from GitHub) before packing a new version.

    Requires the Velopack CLI (vpk). Install once with:  dotnet tool install -g vpk

.PARAMETER Version
    Semantic version of this release, e.g. 1.1.0. Must be higher than the previous release for clients to update.

.PARAMETER Upload
    If set, uploads the release to GitHub Releases (repo VadymKolesnyk/OrientPyx). Needs a token in
    $env:GITHUB_TOKEN with 'repo' scope.

.EXAMPLE
    ./build/publish.ps1 -Version 1.1.0
    ./build/publish.ps1 -Version 1.2.0 -Upload
#>
param(
    [Parameter(Mandatory = $true)] [string] $Version,
    [switch] $Upload
)

$ErrorActionPreference = 'Stop'

$repoRoot   = Split-Path -Parent $PSScriptRoot
$project    = Join-Path $repoRoot 'src/OrientPyx.Presentation/OrientPyx.Presentation.csproj'
$publishDir = Join-Path $repoRoot 'build/publish'
$releaseDir = Join-Path $repoRoot 'build/releases'
$runtime    = 'win-x64'
$packId     = 'OrientPyx'
$packTitle  = 'OrientPyx'

# --- 0. Ensure the vpk tool is available ----------------------------------------------------------
if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
    throw "Velopack CLI (vpk) not found. Install it with:  dotnet tool install -g vpk"
}

# --- 1. Clean publish output ----------------------------------------------------------------------
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
New-Item -ItemType Directory -Path $publishDir | Out-Null

# --- 2. Publish the app (self-contained: end users need no .NET installed) -------------------------
Write-Host "Publishing $packId $Version ($runtime)..." -ForegroundColor Cyan
dotnet publish $project `
    -c Release `
    -r $runtime `
    --self-contained true `
    -p:Version=$Version `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

# --- 3. Pack with Velopack (installer + update package + deltas) -----------------------------------
Write-Host "Packing Velopack release $Version..." -ForegroundColor Cyan
vpk pack `
    --packId $packId `
    --packVersion $Version `
    --packDir $publishDir `
    --mainExe 'OrientPyx.Presentation.exe' `
    --packTitle $packTitle `
    --outputDir $releaseDir
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed." }

Write-Host "Release ready in: $releaseDir" -ForegroundColor Green

# --- 4. (optional) Upload to GitHub Releases ------------------------------------------------------
if ($Upload) {
    if (-not $env:GITHUB_TOKEN) { throw "Set `$env:GITHUB_TOKEN (repo scope) to upload." }
    Write-Host "Uploading release $Version to GitHub..." -ForegroundColor Cyan
    vpk upload github `
        --outputDir $releaseDir `
        --repoUrl 'https://github.com/VadymKolesnyk/OrientPyx' `
        --token $env:GITHUB_TOKEN `
        --publish `
        --releaseName "OrientPyx $Version" `
        --tag "v$Version"
    if ($LASTEXITCODE -ne 0) { throw "vpk upload failed." }
    Write-Host "Uploaded." -ForegroundColor Green
}
