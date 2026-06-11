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

    /// <summary>Loads the current day's groups (one row per group on the day), ordered for display.</summary>
    Task<IReadOnlyList<GroupDayRow>> GetGroupDayRowsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Attaches a group to the current day. Reuses an existing group with the same name
    /// (case-insensitive) or creates one, then ensures a settings row exists for the day.
    /// Returns the resulting row (existing one when already attached).
    /// </summary>
    Task<GroupDayRow> AddGroupToDayAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Attaches every competition group that is not yet on the current day (with blank settings),
    /// then returns the day's full, ordered group set.
    /// </summary>
    Task<IReadOnlyList<GroupDayRow>> PullAllGroupsIntoDayAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists an edited group-day row: renames the group (affects all days) and saves the day's
    /// course order, distance, and discipline override. A rename to a name already used by another
    /// group is ignored (the previous name is kept).
    /// </summary>
    Task UpdateGroupDayRowAsync(GroupDayRow row, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a group from the current day (deletes its settings row). If the group then has no
    /// settings rows left on any day, the group itself is hard-deleted.
    /// </summary>
    Task RemoveGroupFromDayAsync(Guid settingsId, Guid groupId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Imports the courses from a parsed IOF file as groups on the current day. Each course becomes
    /// a group (matched by name, case-insensitive) whose course order is the file's running control
    /// codes, and whose distance is computed from the day's control-point coordinates.
    ///
    /// When <paramref name="updateExisting"/> is true, groups already on the day are updated with the
    /// file's course order and <b>have their discipline override reset to the day default</b>; groups
    /// not yet on the day are created/attached. When false, existing groups are left untouched and
    /// only courses with no matching group on the day are added.
    /// </summary>
    Task<GroupImportResult> ImportGroupsAsync(
        IofCourseData data,
        bool updateExisting,
        CancellationToken cancellationToken = default);

    /// <summary>Loads the current competition's rental chips, ordered by number.</summary>
    Task<IReadOnlyList<RentalChip>> GetRentalChipsAsync(CancellationToken cancellationToken = default);

    /// <summary>Appends a new, blank rental chip to the current competition and returns it.</summary>
    Task<RentalChip> AddRentalChipAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves an edited rental chip (number, note). A change to a number already used by another
    /// chip is ignored (the previous number is kept), keeping numbers unique per competition.
    /// </summary>
    Task UpdateRentalChipAsync(RentalChip chip, CancellationToken cancellationToken = default);

    /// <summary>Removes a rental chip from the current competition.</summary>
    Task DeleteRentalChipAsync(Guid chipId, CancellationToken cancellationToken = default);

    /// <summary>Removes every rental chip from the current competition. Returns how many were deleted.</summary>
    Task<int> ClearRentalChipsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a contiguous range of rental chips numbered from <paramref name="startNumber"/> (e.g.
    /// 100 chips from "9007400"). Numeric increment preserves the start's digit width (leading
    /// zeros). Numbers already present are skipped, never duplicated; the note is applied to the
    /// chips that are added.
    /// </summary>
    Task<RentalChipBulkResult> AddRentalChipRangeAsync(
        string startNumber,
        int count,
        string note,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Imports the chip numbers from a parsed readout file into the rental-chip database. Adds each
    /// distinct number that is not already present (existing chips are left untouched and nothing is
    /// removed); the note is applied to the chips that are added.
    /// </summary>
    Task<RentalChipImportResult> ImportRentalChipsAsync(
        ChipReadData data,
        string note,
        CancellationToken cancellationToken = default);
}

/// <summary>Outcome of a bulk rental-chip range add, for reporting back to the user.</summary>
/// <param name="Added">How many chips were newly added.</param>
/// <param name="Skipped">How many requested numbers already existed and were skipped.</param>
public readonly record struct RentalChipBulkResult(int Added, int Skipped);

/// <summary>Outcome of a rental-chip file import, for reporting back to the user.</summary>
/// <param name="Added">How many chips were newly added.</param>
/// <param name="Skipped">How many distinct numbers in the file already existed and were skipped.</param>
public readonly record struct RentalChipImportResult(int Added, int Skipped);

/// <summary>Outcome of a group import, for reporting back to the user.</summary>
/// <param name="Added">How many groups were newly attached to the day.</param>
/// <param name="Updated">How many existing groups on the day were updated (update mode only).</param>
public readonly record struct GroupImportResult(int Added, int Updated);

/// <summary>Outcome of a control-point import, for reporting back to the user.</summary>
/// <param name="Imported">How many control points ended up in the day after the import.</param>
/// <param name="Added">How many points were newly added (in add-only mode).</param>
/// <param name="Replaced">True when the whole set was replaced.</param>
public readonly record struct ControlPointImportResult(int Imported, int Added, bool Replaced);
