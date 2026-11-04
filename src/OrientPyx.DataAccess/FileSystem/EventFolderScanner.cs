using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.DataAccess.Persistence;

namespace OrientPyx.DataAccess.FileSystem;

/// <summary>
/// Lists subfolders of the events path that contain an event database and reads each
/// competition's metadata to build a selection summary.
/// </summary>
public sealed class EventFolderScanner : IEventFolderScanner
{
    private readonly IEventStore _eventStore;
    private readonly IActivityLog _log;

    public EventFolderScanner(IEventStore eventStore, IActivityLog log)
    {
        _eventStore = eventStore;
        _log = log;
    }

    public async Task<IReadOnlyList<EventSummary>> ScanAsync(string eventsPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(eventsPath) || !Directory.Exists(eventsPath))
            return [];

        var summaries = new List<EventSummary>();

        foreach (var folder in Directory.EnumerateDirectories(eventsPath))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Dot-folders are the archive import's own staging/backup copies, never competitions.
            if (Path.GetFileName(folder).StartsWith('.'))
                continue;

            var dbPath = AppDatabasePaths.GetEventDatabaseFilePath(folder);
            if (!File.Exists(dbPath))
                continue;

            // One unreadable database (corrupt file, data written by a newer version this build cannot
            // map, a locked file) must not hide every other competition or crash the startup restore:
            // log it and skip the folder.
            try
            {
                var summary = await ReadSummaryAsync(folder, cancellationToken);
                if (summary is not null)
                    summaries.Add(summary);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.Error($"Competition folder skipped: {folder}", ex);
            }
        }

        return summaries
            .OrderByDescending(s => s.CreatedAt)
            .ToList();
    }

    private async Task<EventSummary?> ReadSummaryAsync(string folder, CancellationToken cancellationToken)
    {
        // Bring the event database up to the current schema before reading it. The picker reads
        // every event DB at startup; without this, a DB created before a later migration is queried
        // with the new model and fails (e.g. "no such column"). Opening an event later also migrates
        // it, but the scan happens first, so it must migrate too.
        await _eventStore.EnsureCreatedAsync(folder, cancellationToken);

        var info = await _eventStore.GetCompetitionInfoAsync(folder, cancellationToken);
        if (info is null)
            return null;

        var days = await _eventStore.GetDaysAsync(folder, cancellationToken);

        return new EventSummary
        {
            Identifier = info.Identifier,
            Name = info.Name,
            Venue = info.Venue,
            FolderPath = folder,
            CreatedAt = info.CreatedAt,
            DayCount = days.Count,
            StartDate = info.StartDate,
            EndDate = info.EndDate,
            IsHidden = info.IsHidden
        };
    }
}
