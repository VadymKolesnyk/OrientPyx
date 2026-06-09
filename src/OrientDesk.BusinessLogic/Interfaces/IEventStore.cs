using OrientDesk.BusinessLogic.Entities;

namespace OrientDesk.BusinessLogic.Interfaces;

/// <summary>
/// Abstraction over a single competition's database, addressed by its folder path.
/// Implemented in DataAccess; keeps EF Core out of BusinessLogic.
/// </summary>
public interface IEventStore
{
    /// <summary>Creates the event database and schema for a folder if it does not exist.</summary>
    Task EnsureCreatedAsync(string eventFolderPath, CancellationToken cancellationToken = default);

    /// <summary>Reads competition metadata, or null if none is stored.</summary>
    Task<CompetitionInfo?> GetCompetitionInfoAsync(string eventFolderPath, CancellationToken cancellationToken = default);

    /// <summary>Stores (inserts/updates) the single competition metadata row.</summary>
    Task SaveCompetitionInfoAsync(string eventFolderPath, CompetitionInfo info, CancellationToken cancellationToken = default);

    /// <summary>Returns the competition days ordered by number.</summary>
    Task<IReadOnlyList<EventDay>> GetDaysAsync(string eventFolderPath, CancellationToken cancellationToken = default);

    /// <summary>Adds a day to the competition.</summary>
    Task AddDayAsync(string eventFolderPath, EventDay day, CancellationToken cancellationToken = default);

    /// <summary>Updates an existing day's editable fields (date, venue, discipline).</summary>
    Task UpdateDayAsync(string eventFolderPath, EventDay day, CancellationToken cancellationToken = default);

    /// <summary>Removes a day by id. Does nothing if it is missing.</summary>
    Task DeleteDayAsync(string eventFolderPath, Guid dayId, CancellationToken cancellationToken = default);

    /// <summary>Returns a day's control points ordered by their sort order.</summary>
    Task<IReadOnlyList<ControlPoint>> GetControlPointsAsync(string eventFolderPath, Guid dayId, CancellationToken cancellationToken = default);

    /// <summary>Adds a control point to a day.</summary>
    Task AddControlPointAsync(string eventFolderPath, ControlPoint point, CancellationToken cancellationToken = default);

    /// <summary>Adds several control points to a day in one transaction (e.g. an XML import).</summary>
    Task AddControlPointsAsync(string eventFolderPath, IReadOnlyList<ControlPoint> points, CancellationToken cancellationToken = default);

    /// <summary>Deletes a day's existing control points and inserts the supplied set in one transaction.</summary>
    Task ReplaceControlPointsAsync(string eventFolderPath, Guid dayId, IReadOnlyList<ControlPoint> points, CancellationToken cancellationToken = default);

    /// <summary>Updates an existing control point's editable fields (code, coordinates, type).</summary>
    Task UpdateControlPointAsync(string eventFolderPath, ControlPoint point, CancellationToken cancellationToken = default);

    /// <summary>Removes a control point by id. Does nothing if it is missing.</summary>
    Task DeleteControlPointAsync(string eventFolderPath, Guid pointId, CancellationToken cancellationToken = default);
}
