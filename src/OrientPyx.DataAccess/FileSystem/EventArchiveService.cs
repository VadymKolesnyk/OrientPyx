using System.IO.Compression;
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
    private readonly IAppSettingsService _settings;
    private readonly IEventStore _eventStore;

    public EventArchiveService(IAppSettingsService settings, IEventStore eventStore)
    {
        _settings = settings;
        _eventStore = eventStore;
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

        // Zip the whole folder as a single top-level entry named after the identifier, so the identifier
        // is recoverable on import. Overwrite any existing file at the destination. File I/O is blocking,
        // so run it off the calling thread (callers invoke this inside the busy overlay's worker).
        await Task.Run(() =>
        {
            if (File.Exists(destinationArchivePath))
                File.Delete(destinationArchivePath);

            using var zip = ZipFile.Open(destinationArchivePath, ZipArchiveMode.Create);
            foreach (var file in Directory.EnumerateFiles(folderPath, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(folderPath, file);
                // Store under "<identifier>/<relative>" with forward slashes (the zip convention).
                var entryName = $"{identifier}/{relative.Replace(Path.DirectorySeparatorChar, '/')}";

                // The exported competition may be the active session's own competition, whose event.db (and
                // its -wal/-shm sidecars) are held open by the live SQLite connection. ZipFile.CreateEntryFromFile
                // opens the source with FileShare.Read only, which fails ("used by another process") against that
                // open handle. Open the source ourselves with a permissive share so a live DB can still be read.
                var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                using var source = new FileStream(
                    file, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var entryStream = entry.Open();
                source.CopyTo(entryStream);
            }
        }, cancellationToken);
    }

    public async Task<EventArchivePreview> PreviewImportAsync(string archivePath, CancellationToken cancellationToken = default)
    {
        var identifier = await Task.Run(() => ReadArchiveIdentifier(archivePath), cancellationToken);

        var eventsPath = (await _settings.GetPathsAsync(cancellationToken)).EventsPath;
        var exists = Directory.Exists(Path.Combine(eventsPath, identifier));
        return new EventArchivePreview(identifier, exists);
    }

    public async Task<EventSummary> ImportAsync(
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
            // wipe the existing competition.
        }

        await Task.Run(() => ExtractInto(archivePath, folderPath, cancellationToken), cancellationToken);

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

        var days = await _eventStore.GetDaysAsync(folderPath, cancellationToken);
        return new EventSummary
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
    }

    // Extracts the archive's single top-level competition folder into destinationFolder. When the
    // destination exists it is replaced atomically-ish: extract to a sibling temp folder, delete the old,
    // then move the new into place.
    private static void ExtractInto(string archivePath, string destinationFolder, CancellationToken cancellationToken)
    {
        using var zip = ZipFile.OpenRead(archivePath);
        var root = SingleRootFolder(zip);

        var parent = Path.GetDirectoryName(destinationFolder)!;
        var stagingFolder = Path.Combine(parent, $".import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingFolder);
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

                var targetPath = Path.GetFullPath(Path.Combine(stagingFolder, relative));
                // Guard against zip-slip: a crafted entry with ".." must not escape the staging folder.
                if (!targetPath.StartsWith(stagingFull, StringComparison.Ordinal))
                    throw new EventArchiveFormatException("Archive contains an invalid path.");

                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                entry.ExtractToFile(targetPath, overwrite: true);
            }

            if (Directory.Exists(destinationFolder))
                Directory.Delete(destinationFolder, recursive: true);
            Directory.Move(stagingFolder, destinationFolder);
        }
        catch
        {
            if (Directory.Exists(stagingFolder))
                Directory.Delete(stagingFolder, recursive: true);
            throw;
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
