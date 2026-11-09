<#
.SYNOPSIS
    Checks that a signed Windows release is signed throughout, before it is uploaded.

.DESCRIPTION
    Looks at OrientPyx-win-Setup.exe and at every project binary (SigningRules.ps1) inside the full package and
    the portable zip of -Version in -ReleaseDir. A file counts as signed when it carries an Authenticode
    signature at all — a SignPath test-signing certificate is not trusted by Windows, and that is fine for a
    test run. Exits 1 (and annotates the GitHub Actions run) when anything is unsigned or missing.
    Run by .github/workflows/release-windows.yml.
#>
param(
    [Parameter(Mandatory = $true)] [string] $ReleaseDir,
    [Parameter(Mandatory = $true)] [string] $Version
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'SigningRules.ps1')
Add-Type -AssemblyName System.IO.Compression.FileSystem

$problems = [System.Collections.Generic.List[string]]::new()

function Test-Signed([string] $Path, [string] $Label) {
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.SignerCertificate) {
        Write-Host "ok    $Label - $($signature.SignerCertificate.Subject) [$($signature.Status)]"
    } else {
        $problems.Add("$Label is not signed ($($signature.Status))")
        Write-Host "FAIL  $Label"
    }
}

$setup = Join-Path $ReleaseDir 'OrientPyx-win-Setup.exe'
if (Test-Path -LiteralPath $setup) { Test-Signed $setup 'OrientPyx-win-Setup.exe' } else { $problems.Add('OrientPyx-win-Setup.exe is missing') }

$temp = Join-Path ([System.IO.Path]::GetTempPath()) "orientpyx-verify-$([guid]::NewGuid().ToString('N'))"
try {
    foreach ($archive in @("OrientPyx-$Version-full.nupkg", 'OrientPyx-win-Portable.zip')) {
        $archivePath = Join-Path $ReleaseDir $archive
        if (-not (Test-Path -LiteralPath $archivePath)) { $problems.Add("$archive is missing"); continue }
        $dest = Join-Path $temp $archive
        [System.IO.Compression.ZipFile]::ExtractToDirectory($archivePath, $dest)
        $own = @(Get-ChildItem -LiteralPath $dest -Recurse -File | Where-Object { Test-OrientPyxOwnBinary $_.FullName })
        if ($own.Count -eq 0) { $problems.Add("$archive holds none of the project's binaries"); continue }
        foreach ($file in $own) {
            Test-Signed $file.FullName "$archive/$($file.FullName.Substring($dest.Length).TrimStart('\', '/'))"
        }
    }
} finally {
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}

if ($problems.Count -gt 0) {
    $problems | ForEach-Object { Write-Host "::error::$_" }
    exit 1
}
Write-Host 'Every project binary of the release is signed.'
