# Which binaries of the OrientPyx Windows package are the project's own and get code-signed. SignPath Foundation
# signs only those (docs/code-signing.md); third-party libraries (Avalonia, SkiaSharp, SQLite, ClosedXML…) ship
# exactly as their maintainers built them. Dot-sourced by vpk-sign-hook.ps1 and Test-ReleaseSignatures.ps1, so
# what gets signed and what is checked afterwards can never drift apart. Keep it in step with
# artifact-configuration.xml (the same files, as SignPath sees them).

function Test-OrientPyxOwnBinary {
    param([Parameter(Mandatory = $true)] [string] $Path)

    $name = [System.IO.Path]::GetFileName($Path)

    # The app and its libraries: OrientPyx.exe, OrientPyx.dll, OrientPyx.BusinessLogic.dll, ...
    if ($name -match '^OrientPyx(\.[A-Za-z0-9]+)*\.(dll|exe)$') { return $true }

    # Velopack's launcher stub (built from OrientPyx.exe, carries its version resources) and its updater
    # (Update.exe, packaged as Squirrel.exe) — part of every OrientPyx installation.
    if ($name -ieq 'OrientPyx_ExecutionStub.exe' -or $name -ieq 'Squirrel.exe' -or $name -ieq 'Update.exe') { return $true }

    # The standard plugins: plugins\<Name>\<Name>.dll
    $dir = [System.IO.Path]::GetDirectoryName($Path)
    if ($dir) {
        $folder = [System.IO.Path]::GetFileName($dir)
        $parent = [System.IO.Path]::GetFileName([System.IO.Path]::GetDirectoryName($dir))
        if ($parent -ieq 'plugins' -and $name -ieq "$folder.dll") { return $true }
    }

    return $false
}

# The installer Velopack builds last (OrientPyx-win-Setup.exe): signed by its own request after `vpk pack`.
function Test-OrientPyxSetup {
    param([Parameter(Mandatory = $true)] [string] $Path)
    return [System.IO.Path]::GetFileName($Path) -match '-Setup\.exe$'
}
