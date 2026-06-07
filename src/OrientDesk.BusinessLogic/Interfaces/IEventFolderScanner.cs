using OrientDesk.BusinessLogic.Models;

namespace OrientDesk.BusinessLogic.Interfaces;

/// <summary>Scans the events folder for competition folders and builds summaries.</summary>
public interface IEventFolderScanner
{
    /// <summary>Returns a summary per competition folder that contains an event database.</summary>
    Task<IReadOnlyList<EventSummary>> ScanAsync(string eventsPath, CancellationToken cancellationToken = default);
}
