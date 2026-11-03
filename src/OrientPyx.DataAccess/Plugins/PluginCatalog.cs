using Microsoft.EntityFrameworkCore;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.DataAccess.Persistence;

namespace OrientPyx.DataAccess.Plugins;

/// <summary>
/// The plugin catalog: one folder, <c>&lt;data root&gt;/plugins</c>, holding both the user's own plugins and the
/// standard ones — those are copied in from <c>&lt;app dir&gt;/plugins</c> (and, in a Debug build,
/// <c>&lt;repo&gt;/artifacts/plugins</c>) before loading, so an update of the app updates them too.
/// Loading is deferred to first use, which the app triggers right after migrating the app database (the list of
/// switched-on plugins lives there). Load failures are written to the activity log.
/// </summary>
internal sealed class PluginCatalog : IPluginCatalog
{
    public const string FolderName = "plugins";

    private readonly IDbContextFactory<AppDbContext> _appDb;
    private readonly IActivityLog _log;
    private readonly Lazy<(IReadOnlyList<LoadedPlugin> Plugins, IReadOnlyList<PluginLoadError> Errors)> _loaded;
    private readonly Lazy<HashSet<string>> _enabled;
    private readonly Lock _gate = new();

    public PluginCatalog(IDbContextFactory<AppDbContext> appDb, IActivityLog log)
    {
        _appDb = appDb;
        _log = log;
        Folders = [Path.GetFullPath(UserFolder)];
        _loaded = new(Load);
        _enabled = new(() => new HashSet<string>(ReadEnabled(), StringComparer.Ordinal));
    }

    public IReadOnlyList<LoadedPlugin> Plugins => _loaded.Value.Plugins;

    public IReadOnlyList<PluginLoadError> Errors => _loaded.Value.Errors;

    public string UserFolder => Path.Combine(AppDatabasePaths.BaseDirectory, FolderName);

    public IReadOnlyList<string> Folders { get; }

    public event EventHandler? EnabledChanged;

    public bool IsEnabled(string pluginId)
    {
        if (Plugins.All(p => p.Info.Id != pluginId))
            return false;
        lock (_gate)
            return _enabled.Value.Contains(pluginId);
    }

    public async Task SetEnabledAsync(string pluginId, bool enabled, CancellationToken cancellationToken = default)
    {
        string json;
        lock (_gate)
        {
            var set = _enabled.Value;
            if (!(enabled ? set.Add(pluginId) : set.Remove(pluginId)))
                return;
            json = PluginIdList.Serialize(set);
        }

        await using (var db = await _appDb.CreateDbContextAsync(cancellationToken))
        {
            var row = await db.Settings.FirstOrDefaultAsync(cancellationToken);
            if (row is null)
                db.Settings.Add(new AppSettingsRow { Id = 1, EnabledPluginsJson = json });
            else
                row.EnabledPluginsJson = json;
            await db.SaveChangesAsync(cancellationToken);
        }

        _log.Info($"Plugin '{pluginId}' {(enabled ? "enabled" : "disabled")}");
        EnabledChanged?.Invoke(this, EventArgs.Empty);
    }

    private (IReadOnlyList<LoadedPlugin>, IReadOnlyList<PluginLoadError>) Load()
    {
        foreach (var source in BundledFolders())
            SyncBundled(source, Folders[0]);

        var (plugins, errors) = new PluginLoader(new DllPluginSource()).Load(Folders);
        foreach (var p in plugins)
            _log.Info($"Plugin '{p.Info.Id}' {p.Info.Version} from {p.Folder}");
        foreach (var e in errors)
            _log.Info($"Plugin not loaded ({e.PluginId ?? "?"}): {e.Path}: {e.Message}");
        return (plugins, errors);
    }

    private IReadOnlySet<string> ReadEnabled()
    {
        try
        {
            using var db = _appDb.CreateDbContext();
            return PluginIdList.Parse(db.Settings.AsNoTracking().Select(s => s.EnabledPluginsJson).FirstOrDefault());
        }
        catch (Exception ex)
        {
            _log.Error("Reading enabled plugins", ex);
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    /// <summary>Where the standard plugins come from: the app package's folder (and the repository build in Debug).</summary>
    private static IEnumerable<string> BundledFolders()
    {
#if DEBUG
        // A development run picks up the plugins built from the repository's plugins/ solution.
        if (FindRepoRoot() is { } repo)
            yield return Path.Combine(repo, "artifacts", FolderName);
#endif
        yield return Path.Combine(AppContext.BaseDirectory, FolderName);
    }

    /// <summary>
    /// Copies every file of <paramref name="source"/> into <paramref name="target"/> that is missing there or older
    /// than the bundled one — a newer own build in the same place is kept. A locked file (another instance has the
    /// plugin loaded) is skipped: that launch keeps the previous version.
    /// </summary>
    private void SyncBundled(string source, string target)
    {
        source = Path.GetFullPath(source);
        if (!Directory.Exists(source) || string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
            return;

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(target, Path.GetRelativePath(source, file));
            try
            {
                if (File.Exists(dest) && File.GetLastWriteTimeUtc(dest) >= File.GetLastWriteTimeUtc(file))
                    continue;
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(file, dest, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Info($"Plugin file not updated: {dest}: {ex.Message}");
            }
        }
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
