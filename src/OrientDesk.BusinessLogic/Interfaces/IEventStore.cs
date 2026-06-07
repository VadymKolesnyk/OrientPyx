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
}
