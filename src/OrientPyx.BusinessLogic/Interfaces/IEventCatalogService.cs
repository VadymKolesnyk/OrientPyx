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
    /// Renames a competition's identifier: moves its folder to the new name under the events path and
    /// writes the new identifier into the competition metadata (the scanner reads it from the database,
    /// so both must change together). Returns the updated summary.
    /// </summary>
    /// <remarks>
    /// The competition's database files are released first, so the folder can be moved even when this is
    /// the active session's own competition. Throws when the identifier is invalid, unchanged, or already
    /// taken, and when the folder cannot be moved (e.g. another program holds a file in it open).
    /// </remarks>
    Task<EventSummary> RenameIdentifierAsync(
        EventSummary competition,
        string newIdentifier,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a competition from this machine for good: its whole folder under the events path (<c>event.db</c> with
    /// every table in it, the day folders with their imported files, read-outs and monitor pages, the logs, the saved
    /// table views) and the last-session pointer when it names this competition. A folder that is already gone is not
    /// an error.
    /// </summary>
    /// <remarks>
    /// The database files are released first, then the folder is moved aside as a whole before it is deleted: a move is
    /// all-or-nothing, so a competition another OrientPyx window or program holds open stays intact and
    /// <see cref="EventFolderInUseException"/> is thrown. Throws <see cref="StationsActiveException"/> while network
    /// stations work on it, and <see cref="ArgumentException"/> for a folder that is not directly under the events
    /// path. Host-only: a station never deletes the host's competitions (the host refuses the call over the network).
    /// </remarks>
    Task DeleteEventAsync(EventSummary competition, CancellationToken cancellationToken = default);

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
