using OrientDesk.BusinessLogic.Models;

namespace OrientDesk.BusinessLogic.Interfaces;

/// <summary>Lists and creates competitions under the configured events folder.</summary>
public interface IEventCatalogService
{
    /// <summary>Scans the events folder and returns one summary per competition.</summary>
    Task<IReadOnlyList<EventSummary>> GetEventsAsync(CancellationToken cancellationToken = default);

    /// <summary>Finds a single competition by its identifier (folder name), or null.</summary>
    Task<EventSummary?> FindByIdentifierAsync(string identifier, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a competition: a folder named <paramref name="identifier"/> under the events
    /// path, its event database, the metadata row, and an initial day. Throws if the
    /// identifier is invalid or already exists.
    /// </summary>
    Task<EventSummary> CreateEventAsync(string name, string identifier, string venue, CancellationToken cancellationToken = default);
}
