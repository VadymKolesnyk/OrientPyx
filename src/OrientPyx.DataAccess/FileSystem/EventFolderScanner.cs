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

    public EventFolderScanner(IEventStore eventStore)
    {
        _eventStore = eventStore;
    }

    public async Task<IReadOnlyList<EventSummary>> ScanAsync(string eventsPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(eventsPath) || !Directory.Exists(eventsPath))
            return [];

        var summaries = new List<EventSummary>();

        foreach (var folder in Directory.EnumerateDirectories(eventsPath))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var dbPath = AppDatabasePaths.GetEventDatabaseFilePath(folder);
            if (!File.Exists(dbPath))
                continue;

            // Bring the event database up to the current schema before reading it. The picker reads
            // every event DB at startup; without this, a DB created before a later migration is queried
            // with the new model and fails (e.g. "no such column"). Opening an event later also migrates
            // it, but the scan happens first, so it must migrate too.
            await _eventStore.EnsureCreatedAsync(folder, cancellationToken);

            var info = await _eventStore.GetCompetitionInfoAsync(folder, cancellationToken);
            if (info is null)
                continue;

            var days = await _eventStore.GetDaysAsync(folder, cancellationToken);

            summaries.Add(new EventSummary
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
            });
        }

        return summaries
            .OrderByDescending(s => s.CreatedAt)
            .ToList();
    }
}
