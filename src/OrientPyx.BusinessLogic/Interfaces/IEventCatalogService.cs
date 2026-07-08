using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Interfaces;

/// <summary>Lists and creates competitions under the configured events folder.</summary>
public interface IEventCatalogService
{
    /// <summary>Scans the events folder and returns one summary per competition.</summary>
    Task<IReadOnlyList<EventSummary>> GetEventsAsync(CancellationToken cancellationToken = default);

    /// <summary>Finds a single competition by its identifier (folder name), or null.</summary>
    Task<EventSummary?> FindByIdentifierAsync(string identifier, CancellationToken cancellationToken = default);

    /// <summary>
    /// Hides or reveals a competition on the selection list. The flag is stored in the competition's
    /// own database; hidden competitions are shown only when the picker's "show hidden" switch is on.
    /// </summary>
    Task SetHiddenAsync(EventSummary summary, bool hidden, CancellationToken cancellationToken = default);

    /// <summary>
    /// True when <paramref name="identifier"/> is a valid folder name and no competition folder with that
    /// name already exists under the events path. Used to validate the identifier before creating.
    /// </summary>
    Task<bool> IsIdentifierAvailableAsync(string identifier, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a competition: a folder named <paramref name="identifier"/> under the events
    /// path, its event database, the metadata row, and <paramref name="dayCount"/> days. Throws
    /// if the identifier is invalid or already exists.
    /// </summary>
    /// <param name="dayCount">Number of competition days to create (at least 1).</param>
    /// <param name="startDate">
    /// Date of the first day. Each subsequent day is offset by one calendar day. When null, days
    /// are created without dates.
    /// </param>
    /// <param name="endDate">
    /// Last day of the competition, stored as metadata. For a single-day competition this equals
    /// <paramref name="startDate"/>.
    /// </param>
    /// <param name="officials">
    /// Optional officials (course-setter, chief judge, chief secretary, jury) seeded onto the new
    /// competition's metadata. When null, none are set (they can be filled in later on the Information page).
    /// </param>
    Task<EventSummary> CreateEventAsync(
        string name,
        string identifier,
        string venue,
        int dayCount,
        DateTimeOffset? startDate,
        DateTimeOffset? endDate,
        CompetitionOfficials? officials = null,
        CancellationToken cancellationToken = default);
}
