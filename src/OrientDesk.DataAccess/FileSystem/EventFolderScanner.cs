using OrientDesk.BusinessLogic.Interfaces;
using OrientDesk.BusinessLogic.Models;
using OrientDesk.DataAccess.Persistence;

namespace OrientDesk.DataAccess.FileSystem;

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
                DayCount = days.Count
            });
        }

        return summaries
            .OrderByDescending(s => s.CreatedAt)
            .ToList();
    }
}
