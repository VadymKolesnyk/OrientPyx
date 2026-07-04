using System.Diagnostics;
using Avalonia.Platform.Storage;

namespace OrientPyx.Presentation.Views.Pages;

/// <summary>
/// Opens a just-saved protocol file in the OS default application (Word for .docx). Best-effort: a local file
/// path is launched via the shell; anything that isn't a local path, or any launch failure, is silently
/// ignored (the file is already saved — opening it is a convenience, not a requirement).
/// </summary>
internal static class ProtocolFileLauncher
{
    public static void TryOpen(IStorageFile file)
    {
        try
        {
            var path = file.TryGetLocalPath();
            if (string.IsNullOrEmpty(path))
                return;
            // UseShellExecute lets the OS pick the registered handler (Word/LibreOffice) for the .docx.
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch
        {
            // No handler, file moved, sandbox restriction, etc. — opening is optional, so swallow it.
        }
    }
}
