using OrientDesk.BusinessLogic.Entities;

namespace OrientDesk.BusinessLogic.Interfaces;

/// <summary>
/// Reads and edits the current competition's metadata and days, operating on the event
/// selected in <see cref="ISessionService"/>. Keeps event-folder paths and EF Core out of
/// the presentation layer.
/// </summary>
public interface ICompetitionEditorService
{
    /// <summary>Loads the current competition's metadata, or null when nothing is selected.</summary>
    Task<CompetitionInfo?> GetInfoAsync(CancellationToken cancellationToken = default);

    /// <summary>Saves edited competition metadata for the current competition.</summary>
    Task SaveInfoAsync(CompetitionInfo info, CancellationToken cancellationToken = default);

    /// <summary>Loads the current competition's days, ordered by number.</summary>
    Task<IReadOnlyList<EventDay>> GetDaysAsync(CancellationToken cancellationToken = default);

    /// <summary>Appends a new day (numbered after the last one) to the current competition.</summary>
    Task<EventDay> AddDayAsync(CancellationToken cancellationToken = default);

    /// <summary>Saves an edited day (date, venue, discipline).</summary>
    Task UpdateDayAsync(EventDay day, CancellationToken cancellationToken = default);

    /// <summary>Removes a day from the current competition.</summary>
    Task DeleteDayAsync(Guid dayId, CancellationToken cancellationToken = default);
}
