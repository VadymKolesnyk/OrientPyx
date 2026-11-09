<#
.SYNOPSIS
    `vpk pack --signTemplate` hook that lets SignPath sign what Velopack signs: two packs, one signing request.

.DESCRIPTION
    SignPath signs only an artifact uploaded by a GitHub Actions step, while Velopack has to sign during
    `vpk pack`: it adds the updater and the launcher stub itself and hashes the package right after (signing
    later would break the update feed). So .github/workflows/release-windows.yml packs twice from one build:

      Collect  the throwaway pack. Copies every file of the project's own (SigningRules.ps1) to
               <WorkDir>\unsigned and records "SHA-256 -> file name" in <WorkDir>\manifest.json. Signs nothing.
               (The workflow uploads <WorkDir>\unsigned; SignPath returns it signed into <WorkDir>\signed.)
      Swap     the real pack. Replaces each such file with its signed copy, found by the SHA-256 of the unsigned
               bytes. Fails when a file is unknown (the two packs saw different bytes) or a copy is unsigned.

    Third-party binaries are left alone. Setup.exe is skipped by both modes: Velopack builds it after the
    package, and the workflow signs it with a second request once `vpk pack` is done (it is not part of the
    feed's hashes).

    vpk runs the template through cmd.exe, one call per signing phase (build/publish.ps1 passes a large
    --signParallel), e.g.:
        pwsh -NoProfile -File "<repo>\build\signpath\vpk-sign-hook.ps1" -Mode Collect -WorkDir "<dir>" {{file...}}
#>
param(
    [Parameter(Mandatory = $true)] [ValidateSet('Collect', 'Swap')] [string] $Mode,
    [Parameter(Mandatory = $true)] [string] $WorkDir,
    [Parameter(ValueFromRemainingArguments = $true)] [string[]] $Files
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'SigningRules.ps1')

$manifestPath = Join-Path $WorkDir 'manifest.json'
$unsignedDir  = Join-Path $WorkDir 'unsigned'
$signedDir    = Join-Path $WorkDir 'signed'

# SHA-256 (hex) -> file name of every collected file.
$manifest = @{}
if (Test-Path -LiteralPath $manifestPath) {
    (Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json).PSObject.Properties |
        ForEach-Object { $manifest[$_.Name] = [string]$_.Value }
}

$own = @($Files | Where-Object { $_ } | Where-Object {
    if (Test-OrientPyxSetup $_) { Write-Host "skip (signed after packing): $_"; return $false }
    if (-not (Test-OrientPyxOwnBinary $_)) { Write-Host "skip (third-party): $_"; return $false }
    return $true
})

if ($Mode -eq 'Collect') {
    New-Item -ItemType Directory -Path $unsignedDir -Force | Out-Null
    foreach ($file in $own) {
        $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
        $name = [System.IO.Path]::GetFileName($file)
        # The signed copies come back in one flat folder, so two different files may not share a name.
        $clash = $manifest.GetEnumerator() | Where-Object { $_.Value -ieq $name -and $_.Key -ne $hash }
        if ($clash) { throw "Two different files are named '$name' - the flat signing folder cannot hold both." }
        $manifest[$hash] = $name
        Copy-Item -LiteralPath $file -Destination (Join-Path $unsignedDir $name) -Force
        Write-Host "collected: $file"
    }
    $manifest | ConvertTo-Json | Set-Content -LiteralPath $manifestPath -Encoding utf8
    Write-Host "Collected $($own.Count) file(s); $($manifest.Count) in $unsignedDir."
    exit 0
}

# Swap
if ($own.Count -eq 0) { exit 0 }
if ($manifest.Count -eq 0) { throw "No $manifestPath - run the collecting pack (-Mode Collect) first." }

$swapped = 0
foreach ($file in $own) {
    $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
    $name = $manifest[$hash]
    if (-not $name) {
        throw "'$file' differs from every collected file (SHA-256 $hash): the two packs must see identical bytes."
    }
    $signed = Join-Path $signedDir $name
    if (-not (Test-Path -LiteralPath $signed)) { throw "SignPath returned no '$name' (expected in $signedDir)." }
    $signature = Get-AuthenticodeSignature -LiteralPath $signed
    if (-not $signature.SignerCertificate) { throw "'$name' came back unsigned ($($signature.Status))." }
    Copy-Item -LiteralPath $signed -Destination $file -Force
    Write-Host "signed: $file ($($signature.SignerCertificate.Subject))"
    $swapped++
}

# One call gets the whole phase (large --signParallel), so every collected file must have been offered again.
if ($swapped -ne $manifest.Count) {
    throw "Swapped $swapped file(s), but $($manifest.Count) were collected: the two packs offered different files."
}
Write-Host "Swapped in $swapped signed file(s)."
