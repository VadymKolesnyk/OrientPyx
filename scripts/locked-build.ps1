<#
.SYNOPSIS
    Runs a dotnet command (build / test) under a machine-wide lock, so several agents or terminals working in the
    same checkout never build concurrently (concurrent MSBuild runs on one obj/ folder fail with locked files).

.EXAMPLE
    ./scripts/locked-build.ps1 build OrientPyx.sln -nologo -v q
    ./scripts/locked-build.ps1 test tests/OrientPyx.Tests.sln --filter "FullyQualifiedName~ResultsGoldenTests"
#>
$ErrorActionPreference = 'Continue'
$mutex = New-Object System.Threading.Mutex($false, 'Global\OrientPyxBuildLock')
try {
    [void]$mutex.WaitOne()
    Set-Location (Split-Path -Parent $PSScriptRoot)
    & dotnet $args
    exit $LASTEXITCODE
}
finally {
    $mutex.ReleaseMutex()
    $mutex.Dispose()
}
