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

    public static string GetEventConnectionString(string eventFolderPath)
        => $"Data Source={GetEventDatabaseFilePath(eventFolderPath)}";

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
