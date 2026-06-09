using OrientDesk.BusinessLogic.Entities;
using OrientDesk.BusinessLogic.Models;

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

    /// <summary>Loads the current day's control points, ordered for display.</summary>
    Task<IReadOnlyList<ControlPoint>> GetControlPointsAsync(CancellationToken cancellationToken = default);

    /// <summary>Appends a new control point to the current day and returns it.</summary>
    Task<ControlPoint> AddControlPointAsync(CancellationToken cancellationToken = default);

    /// <summary>Saves an edited control point (code, coordinates, type).</summary>
    Task UpdateControlPointAsync(ControlPoint point, CancellationToken cancellationToken = default);

    /// <summary>Removes a control point from the current day.</summary>
    Task DeleteControlPointAsync(Guid pointId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Imports the control points from a parsed IOF file into the current day. When
    /// <paramref name="replaceAll"/> is true the day's existing points are cleared and fully
    /// replaced; otherwise only codes not already present are appended (existing rows untouched).
    /// </summary>
    Task<ControlPointImportResult> ImportControlPointsAsync(
        IofCourseData data,
        bool replaceAll,
        CancellationToken cancellationToken = default);
}

/// <summary>Outcome of a control-point import, for reporting back to the user.</summary>
/// <param name="Imported">How many control points ended up in the day after the import.</param>
/// <param name="Added">How many points were newly added (in add-only mode).</param>
/// <param name="Replaced">True when the whole set was replaced.</param>
public readonly record struct ControlPointImportResult(int Imported, int Added, bool Replaced);
