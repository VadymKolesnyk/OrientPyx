using System.Runtime.InteropServices;
using Microsoft.Win32;
using OrientPyx.Presentation.Services;

namespace OrientPyx.Presentation;

/// <summary>
/// Associates competition archives (<c>.opyx</c>) with the installed app, so double-clicking one in Explorer
/// launches OrientPyx and starts importing it. Per-user (HKCU\Software\Classes — no admin rights needed) and
/// Windows-only; every call is best-effort and never throws.
/// </summary>
internal static class FileAssociation
{
    private const string ProgId = "OrientPyx.Archive";
    private static string ExtensionKey => $@"Software\Classes\.{EventArchiveConstants.Extension}";
    private const string ProgIdKey = @"Software\Classes\" + ProgId;

    /// <summary>Points <c>.opyx</c> at <paramref name="exePath"/>. Idempotent, so it is safe on every launch.</summary>
    public static void Register(string? exePath)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(exePath))
            return;

        try
        {
            using (var ext = Registry.CurrentUser.CreateSubKey(ExtensionKey))
                ext.SetValue(string.Empty, ProgId);

            using (var progId = Registry.CurrentUser.CreateSubKey(ProgIdKey))
            {
                progId.SetValue(string.Empty, "OrientPyx");
                using (var icon = progId.CreateSubKey("DefaultIcon"))
                    icon.SetValue(string.Empty, $"\"{exePath}\",0");
                using (var command = progId.CreateSubKey(@"shell\open\command"))
                    command.SetValue(string.Empty, $"\"{exePath}\" \"%1\"");
            }

            NotifyShell();
        }
        catch
        {
            // Best-effort: the app works without the association.
        }
    }

    /// <summary>Removes the association (on uninstall).</summary>
    public static void Unregister()
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            // Only drop the extension's default if it still points at us — another app may have taken it over.
            using (var ext = Registry.CurrentUser.OpenSubKey(ExtensionKey, writable: true))
            {
                if (ext?.GetValue(string.Empty) as string == ProgId)
                    ext.DeleteValue(string.Empty, throwOnMissingValue: false);
            }
            Registry.CurrentUser.DeleteSubKeyTree(ProgIdKey, throwOnMissingSubKey: false);
            NotifyShell();
        }
        catch
        {
            // Best-effort.
        }
    }

    /// <summary>The competition archive the app was launched to open (a double-clicked <c>.opyx</c>), if any.</summary>
    public static string? FindArchiveArgument(string[] args) =>
        args.FirstOrDefault(a =>
            string.Equals(Path.GetExtension(a), $".{EventArchiveConstants.Extension}", StringComparison.OrdinalIgnoreCase)
            && File.Exists(a));

    // Tells Explorer the association changed, so the new icon/handler shows without a re-login.
    private static void NotifyShell() => SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, IntPtr.Zero, IntPtr.Zero);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
