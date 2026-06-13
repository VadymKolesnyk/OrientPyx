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

    /// <summary>
    /// Changes a day's 1-based number to <paramref name="newNumber"/> and renames its files folder
    /// (<c>day{old}</c> → <c>day{new}</c>) to match. The other days keep their numbers — this is a
    /// single-day re-label, not a re-ordering. Returns the updated day, or null when the change is
    /// rejected (number unchanged, out of range, or already used by another day).
    /// </summary>
    Task<EventDay?> ChangeDayNumberAsync(Guid dayId, int newNumber, CancellationToken cancellationToken = default);

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

    /// <summary>
    /// Saves a file imported for the current day into that day's folder (<c>&lt;event&gt;/day{N}</c>),
    /// e.g. the IOF XML the courses came from. If a file with the same name and identical content is
    /// already there, nothing is written and the existing path is returned. If the name is taken by a
    /// file with different content, a short content hash is appended to keep both. Returns null when no
    /// day is selected.
    /// </summary>
    Task<string?> SaveDayFileAsync(string fileName, byte[] content, CancellationToken cancellationToken = default);

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

    /// <summary>
    /// Maps each chip number that is assigned to one or more participants (on any day) to the
    /// comma-separated full names of those participants, deduplicated and in display order. Chips
    /// nobody holds are absent from the map. Keyed case-insensitively on the trimmed chip number, so a
    /// rental chip can look its holders up directly. Lets the rental-chip grid show who holds each chip.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> GetRentalChipHoldersAsync(CancellationToken cancellationToken = default);

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

    /// <summary>Loads the current competition's regions, ordered by name.</summary>
    Task<IReadOnlyList<Region>> GetRegionsAsync(CancellationToken cancellationToken = default);

    /// <summary>Appends a new, blank region to the current competition (Regions page "+ add") and returns it.</summary>
    Task<Region> AddRegionRowAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a region by name for the current competition: reuses an existing region with the same
    /// name (case-insensitive) or creates one. Returns the resulting region. Used by the "+ новий"
    /// flow on the participants page. A blank name is rejected (returns null).
    /// </summary>
    Task<Region?> AddRegionAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves an edited region (name). A change to a name already used by another region is ignored
    /// (the previous name is kept), keeping names unique per competition.
    /// </summary>
    Task UpdateRegionAsync(Region region, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a region from the current competition, first clearing it from any participant that
    /// referenced it (their region falls back to none).
    /// </summary>
    Task DeleteRegionAsync(Guid regionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Maps each region id to how many participants come from it (across the whole competition).
    /// Regions with no participants map to 0. Lets the regions grid show a participant count.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, int>> GetRegionParticipantCountsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets a participant's region (competition-level, independent of any day). A null region clears it.
    /// </summary>
    Task SetParticipantRegionAsync(Guid participantId, Guid? regionId, CancellationToken cancellationToken = default);

    /// <summary>Loads the current competition's clubs, ordered by name.</summary>
    Task<IReadOnlyList<Club>> GetClubsAsync(CancellationToken cancellationToken = default);

    /// <summary>Appends a new, blank club to the current competition (Clubs page "+ add") and returns it.</summary>
    Task<Club> AddClubRowAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a club by name for the current competition: reuses an existing club with the same name
    /// (case-insensitive) or creates one. Returns the resulting club. Used by the "+ новий" flow on the
    /// participants page. A blank name is rejected (returns null).
    /// </summary>
    Task<Club?> AddClubAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves an edited club (name). A change to a name already used by another club is ignored (the
    /// previous name is kept), keeping names unique per competition.
    /// </summary>
    Task UpdateClubAsync(Club club, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a club from the current competition, first clearing it from any participant that
    /// referenced it (their club falls back to none).
    /// </summary>
    Task DeleteClubAsync(Guid clubId, CancellationToken cancellationToken = default);

    /// <summary>Maps each club id to how many participants belong to it (across the whole competition).</summary>
    Task<IReadOnlyDictionary<Guid, int>> GetClubParticipantCountsAsync(CancellationToken cancellationToken = default);

    /// <summary>Sets a participant's club (competition-level, independent of any day). A null club clears it.</summary>
    Task SetParticipantClubAsync(Guid participantId, Guid? clubId, CancellationToken cancellationToken = default);

    /// <summary>Loads the current day's participants (one row per competitor on the day), ordered for display.</summary>
    Task<IReadOnlyList<ParticipantDayRow>> GetParticipantDayRowsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads the roster ("Мандатка"): one row per competition participant with a cell per day,
    /// aggregating membership and per-day group across every day. Independent of the current day.
    /// </summary>
    Task<IReadOnlyList<ParticipantRosterRow>> GetParticipantRosterAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new, blank participant and attaches it to the current day, assigning the day's first
    /// group (a member always has a group). Returns the resulting row, or null when the day has no
    /// groups yet (a participant cannot be added without a group to put them in).
    /// </summary>
    Task<ParticipantDayRow?> AddParticipantToDayAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new, blank participant that does not yet run on any day (no day links). Used by the
    /// roster ("Мандатка") add: the participant appears as a non-member on every day and is assigned
    /// days by picking a group in the roster's per-day columns. Returns the new participant's roster
    /// row, or null when no competition is selected.
    /// </summary>
    Task<ParticipantRosterRow?> AddRosterParticipantAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists an edited participant-day row: saves the participant identity fields (affecting all
    /// days; a number colliding with another participant is ignored) and the day's group, chip and
    /// team. A chip colliding with another participant on the same day is ignored (the previous value
    /// is kept); both revert on the next reload.
    /// </summary>
    Task UpdateParticipantDayRowAsync(ParticipantDayRow row, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a participant from a day (deletes its link row). If the participant then has no links
    /// left on any day, the participant itself is hard-deleted.
    /// </summary>
    Task RemoveParticipantFromDayAsync(Guid linkId, Guid participantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Hard-deletes a participant entirely (and all their day links), regardless of how many days they
    /// run. Used by the roster ("Мандатка") delete, where a participant may run on zero days and so has
    /// no link to remove. A no-op when the participant does not exist.
    /// </summary>
    Task DeleteParticipantAsync(Guid participantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets a participant's group on a specific day (roster edit). Creates the day link when missing
    /// (joining the day); passing a null group clears the assignment but keeps the membership.
    /// Returns the id of the affected link (the new or existing one).
    /// </summary>
    Task<Guid> SetParticipantDayGroupAsync(Guid participantId, Guid dayId, Guid? groupId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets a participant's chip on a specific day (roster edit). A no-op when the participant is not
    /// a member that day. A chip colliding with another participant on the same day is ignored (the
    /// previous value is kept; the cell reverts on the next reload), keeping chips unique per day.
    /// </summary>
    Task SetParticipantDayChipAsync(Guid participantId, Guid dayId, string chip, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds the participant who already holds <paramref name="chip"/> on <paramref name="dayId"/>,
    /// other than <paramref name="excludeParticipantId"/>. Returns that participant's full name (for a
    /// reassignment prompt), or null when the chip is free (or only held by the excluded participant).
    /// A blank chip is always free.
    /// </summary>
    Task<string?> FindChipHolderAsync(Guid dayId, string chip, Guid excludeParticipantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets a participant's chip on a specific day, taking it away from any OTHER participant who held
    /// the same chip that day (their chip is cleared). The caller is expected to have already confirmed
    /// the reassignment with the user. A no-op when the participant is not a member that day. Returns
    /// the participant id whose chip was cleared (the previous holder), or null when none.
    /// </summary>
    Task<Guid?> ReassignParticipantDayChipAsync(Guid participantId, Guid dayId, string chip, CancellationToken cancellationToken = default);

    /// <summary>
    /// Toggles a chip number's presence in the rental-chip database: adds it when absent, removes it
    /// when present (matched case-insensitively on the trimmed number). Returns true when the chip is
    /// now in the database (was added), false when it was removed (or the number was blank).
    /// </summary>
    Task<bool> ToggleRentalChipAsync(string number, CancellationToken cancellationToken = default);

    /// <summary>Loads the groups attached to a given day (id + name), for the in-cell group dropdown.</summary>
    Task<IReadOnlyList<GroupDayRow>> GetGroupsForDayAsync(Guid dayId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves a participant's identity fields only (surname, name, number, rank, coach, birth date),
    /// used by the roster ("Мандатка") view where there is no single day. A number colliding with
    /// another participant is ignored (the previous value is kept; the row reverts on the next reload).
    /// </summary>
    Task UpdateParticipantIdentityAsync(ParticipantRosterRow row, CancellationToken cancellationToken = default);
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
