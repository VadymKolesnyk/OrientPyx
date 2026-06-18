namespace OrientDesk.DataAccess.Persistence;

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

    private static string BaseDirectory => AppContext.BaseDirectory;

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
}
