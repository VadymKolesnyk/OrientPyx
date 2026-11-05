using System.IO.Compression;
using System.Text.Json;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.DataAccess.Persistence;

namespace OrientPyx.DataAccess.FileSystem;

/// <summary>
/// Exports a competition folder to a single zip archive and imports such an archive back as a new
/// competition. The archive stores the competition folder as one top-level entry named after its
/// identifier, so the identifier travels with the archive and can be previewed before importing.
/// </summary>
public sealed class EventArchiveService : IEventArchiveService
{
    // App-level data the competition refers to (points rules, ranks), stored next to the competition's
    // own files under the archive's top-level folder. Read on import, never extracted into the folder.
    private const string SharedDataEntryName = "app-shared.json";

    // The plugins the competition's disciplines come from ({"requiredPlugins":[{id, version, disciplines}]}), so an
    // import can warn when one is missing here. Read on preview, never extracted into the folder.
    private const string PluginsEntryName = "plugins.json";

    private readonly IAppSettingsService _settings;
    private readonly IEventStore _eventStore;
    private readonly IEventSharedDataService _sharedData;
    private readonly IPluginCatalog _plugins;

    public EventArchiveService(
        IAppSettingsService settings, IEventStore eventStore, IEventSharedDataService sharedData, IPluginCatalog plugins)
    {
        _plugins = plugins;
        _settings = settings;
        _eventStore = eventStore;
        _sharedData = sharedData;
    }

    public bool IsValidIdentifier(string identifier) => EventIdentifier.IsValid(identifier);

    public async Task<bool> IsIdentifierAvailableAsync(string identifier, CancellationToken cancellationToken = default)
    {
        identifier = (identifier ?? string.Empty).Trim();
        if (!EventIdentifier.IsValid(identifier))
            return false;

        var eventsPath = (await _settings.GetPathsAsync(cancellationToken)).EventsPath;
        return !Directory.Exists(Path.Combine(eventsPath, identifier));
    }

    public async Task ExportAsync(string identifier, string destinationArchivePath, CancellationToken cancellationToken = default)
    {
        identifier = (identifier ?? string.Empty).Trim();
        if (!EventIdentifier.IsValid(identifier))
            throw new ArgumentException("Identifier must be a valid folder name.", nameof(identifier));

        var eventsPath = (await _settings.GetPathsAsync(cancellationToken)).EventsPath;
        var folderPath = Path.Combine(eventsPath, identifier);
        if (!Directory.Exists(folderPath))
            throw new InvalidOperationException($"No competition with identifier '{identifier}'.");

        // Fold the write-ahead log back into event.db first, so the archived database is self-contained
        // and holds the latest committed changes even when this is the active session's own competition.
        await _eventStore.CheckpointAsync(folderPath, cancellationToken);

        var sharedJson = JsonSerializer.Serialize(await _sharedData.CollectAsync(folderPath, cancellationToken));
        var pluginsJson = JsonSerializer.Serialize(
            new PluginManifest(await RequiredPluginsAsync(folderPath, cancellationToken)));

        // Zip the whole folder as a single top-level entry named after the identifier, so the identifier
        // is recoverable on import. Overwrite any existing file at the destination. File I/O is blocking,
        // so run it off the calling thread (callers invoke this inside the busy overlay's worker).
        //
        // The database itself is not read from the live file: while stations (or this instance) keep writing, the
        // file plus its WAL may be mid-change. SQLite's backup API copies a consistent image through a connection
        // into a temp file, which is packed under the name event.db; the -wal/-shm sidecars are then not needed.
        var dbPath = Path.Combine(folderPath, AppDatabasePaths.EventDatabaseFileName);
        var dbCopy = Path.Combine(Path.GetTempPath(), $"opyx-export-{Guid.NewGuid():N}.db");
        try
        {
            await Task.Run(() =>
            {
                if (File.Exists(dbPath))
                    BackupDatabase(folderPath, dbCopy);

                if (File.Exists(destinationArchivePath))
                    File.Delete(destinationArchivePath);

                using var zip = ZipFile.Open(destinationArchivePath, ZipArchiveMode.Create);
                foreach (var file in Directory.EnumerateFiles(folderPath, "*", SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var relative = Path.GetRelativePath(folderPath, file);
                    if (IsDatabaseFile(relative))
                        continue;
                    // Store under "<identifier>/<relative>" with forward slashes (the zip convention).
                    AddFile(zip, $"{identifier}/{relative.Replace(Path.DirectorySeparatorChar, '/')}", file);
                }

                if (File.Exists(dbCopy))
                    AddFile(zip, $"{identifier}/{AppDatabasePaths.EventDatabaseFileName}", dbCopy);

                var sharedEntry = zip.CreateEntry($"{identifier}/{SharedDataEntryName}", CompressionLevel.Optimal);
                using (var writer = new StreamWriter(sharedEntry.Open()))
                    writer.Write(sharedJson);

                var pluginsEntry = zip.CreateEntry($"{identifier}/{PluginsEntryName}", CompressionLevel.Optimal);
                using (var writer = new StreamWriter(pluginsEntry.Open()))
                    writer.Write(pluginsJson);
            }, cancellationToken);
        }
        finally
        {
            try
            {
                if (File.Exists(dbCopy))
                    File.Delete(dbCopy);
            }
            catch (IOException)
            {
                // A leftover temp copy is harmless.
            }
        }
    }

    // event.db and its -wal / -shm sidecars at the folder root: replaced in the archive by the backup copy.
    private static bool IsDatabaseFile(string relativePath)
    {
        if (relativePath.Contains(Path.DirectorySeparatorChar) || relativePath.Contains(Path.AltDirectorySeparatorChar))
            return false;
        var db = AppDatabasePaths.EventDatabaseFileName;
        return string.Equals(relativePath, db, StringComparison.OrdinalIgnoreCase)
               || string.Equals(relativePath, db + "-wal", StringComparison.OrdinalIgnoreCase)
               || string.Equals(relativePath, db + "-shm", StringComparison.OrdinalIgnoreCase);
    }

    // A consistent copy of the live database (WAL included) via the SQLite online backup API. The copy's connection
    // is unpooled so the file is closed — and deletable — as soon as it is done.
    private static void BackupDatabase(string folderPath, string copyPath)
    {
        using var source = new Microsoft.Data.Sqlite.SqliteConnection(AppDatabasePaths.GetEventConnectionString(folderPath));
        using var target = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={copyPath};Pooling=False");
        source.Open();
        target.Open();
        source.BackupDatabase(target);
    }

    private static void AddFile(ZipArchive zip, string entryName, string file)
    {
        // Other files of the folder may be held open by this process (views.json, logs): open them with a permissive
        // share instead of ZipFile.CreateEntryFromFile (FileShare.Read only, which fails against an open handle).
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var entryStream = entry.Open();
        source.CopyTo(entryStream);
    }

    public async Task<EventArchivePreview> PreviewImportAsync(string archivePath, CancellationToken cancellationToken = default)
    {
        var identifier = await Task.Run(() => ReadArchiveIdentifier(archivePath), cancellationToken);
        var required = await Task.Run(() => ReadRequiredPlugins(archivePath), cancellationToken);

        var eventsPath = (await _settings.GetPathsAsync(cancellationToken)).EventsPath;
        var exists = Directory.Exists(Path.Combine(eventsPath, identifier));
        var available = _plugins.Plugins.Where(p => _plugins.IsEnabled(p.Info.Id)).Select(p => p.Info.Id).ToHashSet(StringComparer.Ordinal);
        return new EventArchivePreview(identifier, exists)
        {
            MissingPlugins = required.Where(r => !available.Contains(r.Id)).ToList()
        };
    }

    public async Task<EventArchiveImportResult> ImportAsync(
        string archivePath,
        string identifier,
        bool overwrite,
        CancellationToken cancellationToken = default)
    {
        identifier = (identifier ?? string.Empty).Trim();
        if (!EventIdentifier.IsValid(identifier))
            throw new ArgumentException("Identifier must be a valid folder name.", nameof(identifier));

        var eventsPath = (await _settings.GetPathsAsync(cancellationToken)).EventsPath;
        Directory.CreateDirectory(eventsPath);
        var folderPath = Path.Combine(eventsPath, identifier);

        if (Directory.Exists(folderPath))
        {
            if (!overwrite)
                throw new InvalidOperationException($"A competition with identifier '{identifier}' already exists.");
            // Replacing: extract to a temp folder first, then swap, so a failed/partial extract can't
            // wipe the existing competition. Drop this process's pooled handles on its event.db first —
            // the competition list scan leaves them open, and Windows won't move a folder holding them.
            await _eventStore.ReleaseAsync(folderPath, cancellationToken);
        }

        var sharedData = await Task.Run(() => ExtractInto(archivePath, folderPath, cancellationToken), cancellationToken);

        // The archive's event.db may predate a later migration; bring it up to the current schema before
        // it is opened, matching the folder scan's behaviour.
        await _eventStore.EnsureCreatedAsync(folderPath, cancellationToken);

        // Persist the new identifier into the competition metadata so it matches its (possibly renamed)
        // folder — the scanner reads the identifier from the DB, not the folder name.
        var info = await _eventStore.GetCompetitionInfoAsync(folderPath, cancellationToken);
        if (info is null)
            throw new EventArchiveFormatException("Archive has no competition database.");

        if (!string.Equals(info.Identifier, identifier, StringComparison.Ordinal))
        {
            info.Identifier = identifier;
            await _eventStore.SaveCompetitionInfoAsync(folderPath, info, cancellationToken);
        }

        // Recreate locally the app-level rules/ranks the competition refers to (archives made before this
        // existed carry none — nothing to do then).
        var shared = await _sharedData.ApplyAsync(folderPath, sharedData ?? EventSharedData.Empty, cancellationToken);

        var days = await _eventStore.GetDaysAsync(folderPath, cancellationToken);
        var summary = new EventSummary
        {
            Identifier = identifier,
            Name = info.Name,
            Venue = info.Venue,
            FolderPath = folderPath,
            CreatedAt = info.CreatedAt,
            DayCount = days.Count,
            StartDate = info.StartDate,
            EndDate = info.EndDate
        };
        return new EventArchiveImportResult(summary, shared);
    }

    // Extracts the archive's single top-level competition folder into destinationFolder. When the
    // destination exists it is replaced atomically-ish: extract to a sibling temp folder, delete the old,
    // then move the new into place. Returns the archive's shared app-level data, if it carries any.
    private static EventSharedData? ExtractInto(string archivePath, string destinationFolder, CancellationToken cancellationToken)
    {
        using var zip = ZipFile.OpenRead(archivePath);
        var root = SingleRootFolder(zip);

        var parent = Path.GetDirectoryName(destinationFolder)!;
        var stagingFolder = Path.Combine(parent, $".import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingFolder);
        EventSharedData? sharedData = null;
        try
        {
            var stagingFull = Path.GetFullPath(stagingFolder + Path.DirectorySeparatorChar);
            foreach (var entry in zip.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Strip the archive's top-level folder so the competition's own contents land directly in
                // the staging folder (which becomes the destination folder).
                var relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                if (root.Length > 0 && relative.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    relative = relative[(root.Length + 1)..];

                if (relative.Length == 0 || relative.EndsWith(Path.DirectorySeparatorChar))
                    continue; // directory entry

                if (string.Equals(relative, SharedDataEntryName, StringComparison.OrdinalIgnoreCase))
                {
                    sharedData = ReadSharedData(entry);
                    continue;
                }
                if (string.Equals(relative, PluginsEntryName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var targetPath = Path.GetFullPath(Path.Combine(stagingFolder, relative));
                // Guard against zip-slip: a crafted entry with ".." must not escape the staging folder.
                if (!targetPath.StartsWith(stagingFull, StringComparison.Ordinal))
                    throw new EventArchiveFormatException("Archive contains an invalid path.");

                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                entry.ExtractToFile(targetPath, overwrite: true);
            }

            ReplaceFolder(stagingFolder, destinationFolder);
            return sharedData;
        }
        catch
        {
            if (Directory.Exists(stagingFolder))
                Directory.Delete(stagingFolder, recursive: true);
            throw;
        }
    }

    // Moves stagingFolder into destinationFolder's place. An existing destination is first moved aside as a
    // whole rather than deleted in place: a recursive delete removes files one by one and fails midway on a
    // file another instance holds open (event.db), leaving a half-wiped competition. A folder move is
    // all-or-nothing, so if anything inside is in use the existing competition stays intact.
    private static void ReplaceFolder(string stagingFolder, string destinationFolder)
    {
        if (!Directory.Exists(destinationFolder))
        {
            Directory.Move(stagingFolder, destinationFolder);
            return;
        }

        var parent = Path.GetDirectoryName(destinationFolder)!;
        var backupFolder = Path.Combine(parent, $".replaced-{Guid.NewGuid():N}");
        try
        {
            Directory.Move(destinationFolder, backupFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new EventFolderInUseException($"Competition folder '{destinationFolder}' is in use.", ex);
        }

        try
        {
            Directory.Move(stagingFolder, destinationFolder);
        }
        catch
        {
            Directory.Move(backupFolder, destinationFolder);
            throw;
        }

        try
        {
            Directory.Delete(backupFolder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The new competition is in place; a leftover hidden backup folder is harmless.
        }
    }

    private sealed record PluginManifest(IReadOnlyList<RequiredPlugin> RequiredPlugins);

    // The non-built-in disciplines of every day and group, grouped by the plugin that provides them (its id from
    // the catalog when loaded here, else the id's prefix before the first dot).
    private async Task<IReadOnlyList<RequiredPlugin>> RequiredPluginsAsync(string folderPath, CancellationToken cancellationToken)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var day in await _eventStore.GetDaysAsync(folderPath, cancellationToken))
        {
            used.Add(day.DefaultDiscipline.Value);
            foreach (var gs in await _eventStore.GetGroupDaySettingsAsync(folderPath, day.Id, cancellationToken))
                if (gs.DisciplineOverride is { } o)
                    used.Add(o.Value);
        }

        var builtins = Enum.GetNames<BuiltinDiscipline>().ToHashSet(StringComparer.Ordinal);
        return used
            .Where(id => !builtins.Contains(id))
            .Select(id =>
            {
                var plugin = _plugins.Plugins.FirstOrDefault(p => p.Disciplines.Any(d => d.Id == id));
                return (PluginId: plugin?.Info.Id ?? id.Split('.')[0], Version: plugin?.Info.Version ?? string.Empty, Id: id);
            })
            .GroupBy(x => x.PluginId)
            .Select(g => new RequiredPlugin(g.Key, g.First().Version, g.Select(x => x.Id).Order(StringComparer.Ordinal).ToList()))
            .ToList();
    }

    private static IReadOnlyList<RequiredPlugin> ReadRequiredPlugins(string archivePath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(archivePath);
            var entry = zip.Entries.FirstOrDefault(e =>
                string.Equals(Path.GetFileName(e.FullName), PluginsEntryName, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
                return [];
            using var stream = entry.Open();
            return JsonSerializer.Deserialize<PluginManifest>(stream)?.RequiredPlugins ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException)
        {
            return []; // a damaged manifest must not block importing the competition itself
        }
    }

    private static EventSharedData? ReadSharedData(ZipArchiveEntry entry)
    {
        try
        {
            using var stream = entry.Open();
            return JsonSerializer.Deserialize<EventSharedData>(stream);
        }
        catch (JsonException)
        {
            return null; // a damaged manifest must not block importing the competition itself
        }
    }

    // Reads the identifier an archive would import as: the name of its single top-level folder. Also
    // validates that the archive holds a competition database.
    private static string ReadArchiveIdentifier(string archivePath)
    {
        using var zip = ZipFile.OpenRead(archivePath);
        var root = SingleRootFolder(zip);
        if (root.Length == 0)
            throw new EventArchiveFormatException("Archive has no top-level competition folder.");

        var hasDb = zip.Entries.Any(e =>
            string.Equals(Path.GetFileName(e.FullName), AppDatabasePaths.EventDatabaseFileName, StringComparison.OrdinalIgnoreCase));
        if (!hasDb)
            throw new EventArchiveFormatException("Archive has no competition database.");

        return root;
    }

    // Returns the single top-level folder name shared by every entry, or "" when the entries don't all
    // sit under one folder (not a competition archive we produced).
    private static string SingleRootFolder(ZipArchive zip)
    {
        string? root = null;
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            var slash = name.IndexOf('/');
            if (slash <= 0)
                return string.Empty; // an entry not under any folder — reject
            var top = name[..slash];
            if (root is null)
                root = top;
            else if (!string.Equals(root, top, StringComparison.Ordinal))
                return string.Empty; // more than one top-level folder — reject
        }
        return root ?? string.Empty;
    }
}
