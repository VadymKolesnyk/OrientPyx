namespace OrientPyx.DataAccess.Persistence;

/// <summary>
/// Resolves default locations relative to the application directory.
/// The app database lives in the data folder; events live in the events folder.
/// </summary>
public static class AppDatabasePaths
{
    public const string DefaultDataFolderName = "data";
    public const string DefaultEventsFolderName = "events";
    public const string AppDatabaseFileName = "app.db";
    public const string EventDatabaseFileName = "event.db";

    private static string? _overrideBaseDirectory;

    /// <summary>
    /// The root under which <c>data</c>, <c>events</c> and diagnostic logs live. By default this is the
    /// application directory (so a <c>dotnet run</c> / xcopy build keeps its files next to the exe). An
    /// installed build calls <see cref="UseDataRoot"/> at startup to point it at a stable per-user folder
    /// (Windows <c>%LocalAppData%\OrientPyx-data</c>, outside the Velopack install folder) so competition data
    /// survives auto-updates, which replace the application directory wholesale, and an uninstall.
    /// </summary>
    public static string BaseDirectory => _overrideBaseDirectory ?? AppContext.BaseDirectory;

    /// <summary>
    /// Redirects the data root (see <see cref="BaseDirectory"/>). Call once at startup, before any
    /// database or log is opened. The folder is created if missing.
    /// </summary>
    public static void UseDataRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        Directory.CreateDirectory(path);
        _overrideBaseDirectory = path;
    }

    private static string[] _legacyDataRoots = [];

    /// <summary>
    /// Data roots an earlier build used (e.g. <c>%LocalAppData%\OrientPyx\data-root</c>, inside the Velopack install
    /// folder that an uninstall deletes). A stored path under one of them is read as the same path under
    /// <see cref="BaseDirectory"/> (see <see cref="RemapLegacyPath"/>) — <see cref="MoveLegacyDataRoot"/> moved the
    /// files there.
    /// </summary>
    public static void UseLegacyDataRoots(params string[] roots) => _legacyDataRoots = roots;

    /// <summary>
    /// One-time move of a whole data root from <paramref name="legacyRoot"/> to <paramref name="targetRoot"/>
    /// (a rename on the same volume). Returns the root to use: <paramref name="targetRoot"/> when it already exists
    /// (it wins; the legacy one is left alone), when there was nothing to move, or after a successful move;
    /// <paramref name="legacyRoot"/> when the move failed (a file is open in another instance) — the app keeps
    /// working there and the next launch retries. Never copies: a half-copied open SQLite database is worse than
    /// one more launch from the old place.
    /// </summary>
    public static string MoveLegacyDataRoot(string legacyRoot, string targetRoot, Action<string>? log = null)
    {
        try
        {
            if (Directory.Exists(targetRoot) && Directory.EnumerateFileSystemEntries(targetRoot).Any())
            {
                if (Directory.Exists(legacyRoot))
                    log?.Invoke($"Both data roots exist; using {targetRoot}, leaving {legacyRoot} untouched.");
                return targetRoot;
            }
            if (!Directory.Exists(legacyRoot))
                return targetRoot;

            if (Directory.Exists(targetRoot))
                Directory.Delete(targetRoot);            // an empty leftover would block the rename
            var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(targetRoot));
            if (!string.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);
            Directory.Move(legacyRoot, targetRoot);
            log?.Invoke($"Moved the data root {legacyRoot} → {targetRoot}.");
            return targetRoot;
        }
        catch (Exception ex)
        {
            log?.Invoke($"Could not move the data root {legacyRoot} → {targetRoot} ({ex.Message}); using it in place this launch.");
            return Directory.Exists(legacyRoot) ? legacyRoot : targetRoot;
        }
    }

    /// <summary>
    /// A stored absolute path under a legacy data root (<see cref="UseLegacyDataRoots"/>), rewritten under
    /// <see cref="BaseDirectory"/>; any other path unchanged.
    /// </summary>
    public static string RemapLegacyPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
            return path;
        foreach (var legacy in _legacyDataRoots)
        {
            var root = Path.TrimEndingDirectorySeparator(legacy);
            if (string.Equals(root, Path.TrimEndingDirectorySeparator(BaseDirectory), StringComparison.OrdinalIgnoreCase))
                continue;
            var candidate = Path.TrimEndingDirectorySeparator(path);
            if (candidate.Equals(root, StringComparison.OrdinalIgnoreCase))
                return BaseDirectory;
            if (candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return Path.Combine(BaseDirectory, candidate[(root.Length + 1)..]);
        }
        return path;
    }

    /// <summary>Absolute.</summary>
    public static string DefaultDataPath => Path.Combine(BaseDirectory, DefaultDataFolderName);

    /// <summary>Absolute.</summary>
    public static string DefaultEventsPath => Path.Combine(BaseDirectory, DefaultEventsFolderName);

    /// <summary>Full path to the app database, ensuring its folder exists.</summary>
    public static string GetAppDatabaseFilePath()
    {
        Directory.CreateDirectory(DefaultDataPath);
        return Path.Combine(DefaultDataPath, AppDatabaseFileName);
    }

    public static string GetAppConnectionString() => $"Data Source={GetAppDatabaseFilePath()}";

    /// <summary>Full path to a competition's event database file inside its folder.</summary>
    public static string GetEventDatabaseFilePath(string eventFolderPath)
        => Path.Combine(eventFolderPath, EventDatabaseFileName);

    // Each store call opens its own connection, so a background writer (the finish/chip auto-read
    // poller) and a foreground reader (a page load) can hit the same file at once. Without a busy
    // timeout SQLite returns BUSY immediately ("database is locked") and the operation fails; the
    // page that was loading then sticks on its overlay. The timeout lets a brief collision wait for
    // the lock instead of throwing.
    private const int EventBusyTimeoutMs = 5000;

    public static string GetEventConnectionString(string eventFolderPath)
        => $"Data Source={GetEventDatabaseFilePath(eventFolderPath)};Default Timeout={EventBusyTimeoutMs / 1000}";

    /// <summary>Resolves a possibly-relative configured path against the application directory.</summary>
    public static string ResolvePath(string configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
            return DefaultEventsPath;

        return Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.GetFullPath(Path.Combine(BaseDirectory, configuredPath));
    }

    /// <summary>
    /// True if <paramref name="path"/> lives inside the application (install) directory. On an installed
    /// build that directory is Velopack's <c>...\current</c>, which every auto-update replaces wholesale —
    /// so a stored path pointing there is a data-loss trap and must be rejected in favour of the default
    /// (which resolves to the update-safe data-root). Best-effort: a malformed path is treated as unsafe.
    /// </summary>
    public static bool IsInsideApplicationDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            var appDir = Path.GetFullPath(AppContext.BaseDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var candidate = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return candidate.Equals(appDir, StringComparison.OrdinalIgnoreCase)
                || candidate.StartsWith(appDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true;
        }
    }
}
