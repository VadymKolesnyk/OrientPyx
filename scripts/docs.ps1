<#
.SYNOPSIS
    Previews or builds the documentation site (website/) with MkDocs Material, rendering the app's screenshots first.

.DESCRIPTION
    Screenshots and demo competitions (website/content/shots, website/content/demo — never committed) are rendered by
    the headless app: the DocShots tests in tests/OrientPyx.Tests (DocShots/DocShotTests.cs), run under the build lock.
    They are rendered when -Shots is given or when there are none yet; -NoShots skips them.

    On first run creates .venv-docs in the repository root and installs website/requirements.txt into it (again
    whenever the requirements file changes). Without -Build starts a live preview on http://127.0.0.1:8000;
    -Build writes website/site with --strict, exactly as the GitHub Pages workflow does.

.EXAMPLE
    scripts/docs.ps1                  # preview (renders the screenshots once if missing)
    scripts/docs.ps1 -Shots -Build    # fresh screenshots + the strict CI build
#>
param([switch]$Build, [switch]$Shots, [switch]$NoShots)

# No 'Stop': MkDocs, pip and dotnet log to stderr, which Windows PowerShell turns into errors once the output is
# redirected. Every native call is checked by its exit code instead.
$root = Split-Path -Parent $PSScriptRoot
$content = Join-Path $root 'website/content'
$venv = Join-Path $root '.venv-docs'
$requirements = Join-Path $root 'website/requirements.txt'
$config = Join-Path $root 'website/mkdocs.yml'
$onWindows = $env:OS -eq 'Windows_NT'
$bin = if ($onWindows) { Join-Path $venv 'Scripts' } else { Join-Path $venv 'bin' }
$marker = Join-Path $venv '.requirements-installed'

$shotsDir = Join-Path $content 'shots'
$haveShots = Test-Path (Join-Path $shotsDir 'manifest.json')
if ($Shots -or (-not $NoShots -and -not $haveShots)) {
    foreach ($dir in @($shotsDir, (Join-Path $content 'demo'))) {
        if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
    }
    $env:ORIENTPYX_DOCSHOTS = $content
    # No real competitions from tests/fixtures on the screenshots: point the fixtures at a folder that does not exist.
    $env:ORIENTPYX_TEST_FIXTURES = Join-Path $root 'website/.no-fixtures'
    & (Join-Path $PSScriptRoot 'locked-build.ps1') test (Join-Path $root 'tests/OrientPyx.Tests.sln') --filter 'Category=DocShots' --nologo
    $code = $LASTEXITCODE
    Remove-Item Env:ORIENTPYX_DOCSHOTS, Env:ORIENTPYX_TEST_FIXTURES -ErrorAction SilentlyContinue
    if ($code -ne 0) { throw "Rendering the screenshots failed (dotnet test exit code $code)." }
}

if (-not (Test-Path $bin)) {
    $python = if ($onWindows) { 'python' } else { 'python3' }
    & $python -m venv $venv
    if ($LASTEXITCODE -ne 0) { throw "'$python -m venv' failed - is Python 3 installed?" }
}

if (-not (Test-Path $marker) -or (Get-Item $requirements).LastWriteTimeUtc -gt (Get-Item $marker).LastWriteTimeUtc) {
    & (Join-Path $bin 'python') -m pip install --quiet --disable-pip-version-check -r $requirements
    if ($LASTEXITCODE -ne 0) { throw 'pip install failed' }
    Set-Content -Path $marker -Value (Get-Date -Format o)
}

$mkdocs = Join-Path $bin 'mkdocs'
if ($Build) {
    & $mkdocs build --strict -f $config
} else {
    & $mkdocs serve -f $config
}
exit $LASTEXITCODE
