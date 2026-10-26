using Microsoft.EntityFrameworkCore;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.DataAccess.Persistence;

namespace OrientPyx.DataAccess.Plugins;

/// <summary>
/// The plugin catalog: scans (in a Debug build) <c>&lt;repo&gt;/artifacts/plugins</c>, then
/// <c>&lt;app dir&gt;/plugins</c>, then <c>&lt;data root&gt;/plugins</c> — later folders win on a duplicate id (Р-12).
/// Loading is deferred to first use, which the app triggers right after migrating the app database (the disabled
/// list lives there). Load failures are written to the activity log.
/// </summary>
internal sealed class PluginCatalog : IPluginCatalog
{
    public const string FolderName = "plugins";

    private readonly IDbContextFactory<AppDbContext> _appDb;
    private readonly IActivityLog _log;
    private readonly Lazy<(IReadOnlyList<LoadedPlugin> Plugins, IReadOnlyList<PluginLoadError> Errors)> _loaded;

    public PluginCatalog(IDbContextFactory<AppDbContext> appDb, IActivityLog log)
    {
        _appDb = appDb;
        _log = log;
        Folders = ScanFolders();
        _loaded = new(Load);
    }

    public IReadOnlyList<LoadedPlugin> Plugins => _loaded.Value.Plugins;

    public IReadOnlyList<PluginLoadError> Errors => _loaded.Value.Errors;

    public string UserFolder => Path.Combine(AppDatabasePaths.BaseDirectory, FolderName);

    public IReadOnlyList<string> Folders { get; }

    private (IReadOnlyList<LoadedPlugin>, IReadOnlyList<PluginLoadError>) Load()
    {
        var (plugins, errors) = new PluginLoader(new DllPluginSource()).Load(Folders, ReadDisabled());
        foreach (var p in plugins)
            _log.Info($"Plugin '{p.Info.Id}' {p.Info.Version} from {p.Folder}{(p.Enabled ? string.Empty : " (disabled)")}");
        foreach (var e in errors)
            _log.Info($"Plugin not loaded ({e.PluginId ?? "?"}): {e.Path}: {e.Message}");
        return (plugins, errors);
    }

    private IReadOnlySet<string> ReadDisabled()
    {
        try
        {
            using var db = _appDb.CreateDbContext();
            return PluginIdList.Parse(db.Settings.AsNoTracking().Select(s => s.DisabledPluginsJson).FirstOrDefault());
        }
        catch (Exception ex)
        {
            _log.Error("Reading disabled plugins", ex);
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    private List<string> ScanFolders()
    {
        var folders = new List<string>();
#if DEBUG
        // A development run picks up the plugins built from the repository's plugins/ solution.
        if (FindRepoRoot() is { } repo)
            folders.Add(Path.Combine(repo, "artifacts", FolderName));
#endif
        folders.Add(Path.Combine(AppContext.BaseDirectory, FolderName));
        folders.Add(UserFolder);
        return folders.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

#if DEBUG
    private static string? FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "OrientPyx.sln")))
                return dir.FullName;
        return null;
    }
#endif
}
