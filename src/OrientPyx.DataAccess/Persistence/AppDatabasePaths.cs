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
    /// (e.g. <c>%LocalAppData%\OrientPyx\data-root</c>) so competition data survives auto-updates, which
    /// replace the application directory wholesale.
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

    /// <summary>Default ./data path (absolute).</summary>
    public static string DefaultDataPath => Path.Combine(BaseDirectory, DefaultDataFolderName);

    /// <summary>Default ./events path (absolute).</summary>
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
