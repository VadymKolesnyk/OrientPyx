using System.Diagnostics;

namespace OrientPyx.Presentation.Services;

/// <summary>
/// Shows a file in the OS file manager (Explorer / Finder) with the file pre-selected, falling back to
/// opening its parent folder. Best-effort: a launch failure is swallowed — revealing a file is a
/// convenience, never part of the work the operator is doing.
/// </summary>
internal static class FileReveal
{
    /// <summary>Reveals <paramref name="path"/>: Explorer «/select» on Windows, Finder «open -R» on macOS,
    /// the parent folder elsewhere (or whenever the file itself does not exist yet).</summary>
    public static void TryReveal(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            if (File.Exists(path))
            {
                if (OperatingSystem.IsWindows())
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                    return;
                }

                if (OperatingSystem.IsMacOS())
                {
                    Process.Start(new ProcessStartInfo("open") { ArgumentList = { "-R", path }, UseShellExecute = false });
                    return;
                }
            }

            TryOpenFolder(Path.GetDirectoryName(path));
        }
        catch
        {
            // No file manager, sandbox restriction, etc. — nothing to recover.
        }
    }

    /// <summary>Opens a folder in the OS file manager. Best-effort, never throws.</summary>
    public static void TryOpenFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return;

        try
        {
            // UseShellExecute lets every OS pick its file manager for a directory path.
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch
        {
            // Opening the folder is optional.
        }
    }
}
