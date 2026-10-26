# Compares two results snapshots written by the Debug menu item «Файл → (Debug) Знімок результатів…»
# (events/<id>/_debug/results-*.json). Empty output = identical results.
#
#   powershell -File scripts/diff-results.ps1 <before.json> <after.json>
param(
    [Parameter(Mandatory = $true)][string]$Before,
    [Parameter(Mandatory = $true)][string]$After
)

git diff --no-index --stat -- $Before $After
git diff --no-index -- $Before $After
if ($LASTEXITCODE -eq 0) { Write-Host "No differences." }
