using OrientDesk.BusinessLogic.Entities;
using OrientDesk.BusinessLogic.Enums;
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
    /// Loads every competition group (ordered by name), regardless of day. Used by the «Стартові внески»
    /// page, which sets a per-group entry fee shared across all days.
    /// </summary>
    Task<IReadOnlyList<Group>> GetGroupsAsync(CancellationToken cancellationToken = default);

    /// <summary>Sets a group's base entry fee (shared across all days).</summary>
    Task UpdateGroupEntryFeeAsync(Guid groupId, decimal? entryFee, CancellationToken cancellationToken = default);

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

    /// <summary>Loads the current competition's sports schools (ДЮСШ), ordered by name.</summary>
    Task<IReadOnlyList<Dussh>> GetDusshesAsync(CancellationToken cancellationToken = default);

    /// <summary>Appends a new, blank sports school to the current competition (ДЮСШ page "+ add") and returns it.</summary>
    Task<Dussh> AddDusshRowAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a sports school by name for the current competition: reuses an existing one with the
    /// same name (case-insensitive) or creates it. Returns the resulting school. Used by the "+ новий"
    /// flow on the participants page and by the participant import. A blank name is rejected (returns null).
    /// </summary>
    Task<Dussh?> AddDusshAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves an edited sports school (name). A change to a name already used by another school is
    /// ignored (the previous name is kept), keeping names unique per competition.
    /// </summary>
    Task UpdateDusshAsync(Dussh dussh, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a sports school from the current competition, first clearing it from any participant
    /// that referenced it (their school falls back to none).
    /// </summary>
    Task DeleteDusshAsync(Guid dusshId, CancellationToken cancellationToken = default);

    /// <summary>Maps each sports-school id to how many participants attend it (across the whole competition).</summary>
    Task<IReadOnlyDictionary<Guid, int>> GetDusshParticipantCountsAsync(CancellationToken cancellationToken = default);

    /// <summary>Sets a participant's sports school (competition-level, independent of any day). A null school clears it.</summary>
    Task SetParticipantDusshAsync(Guid participantId, Guid? dusshId, CancellationToken cancellationToken = default);

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
    /// Sets the chip on many participant-day links at once, in a single transaction (used by bulk chip
    /// assignment, which only hands out unused chips, so there is no collision check). Each tuple is
    /// (participantId, dayId, chip); a missing link is skipped. Returns how many links were updated.
    /// </summary>
    Task<int> SetParticipantDayChipsBatchAsync(
        IReadOnlyList<(Guid ParticipantId, Guid DayId, string Chip)> assignments,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets a participant's start time on a specific day (roster edit). A no-op when the participant is
    /// not a member that day. No uniqueness rule, so it is stored directly. A null time clears it.
    /// </summary>
    Task SetParticipantDayStartTimeAsync(Guid participantId, Guid dayId, TimeSpan? startTime, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets a participant's "out of competition" (поза конкурсом) flag on a specific day (roster edit).
    /// A no-op when the participant is not a member that day.
    /// </summary>
    Task SetParticipantDayOutOfCompetitionAsync(Guid participantId, Guid dayId, bool outOfCompetition, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets (or clears, when null) a judge's manual finish-status override for a participant on a day,
    /// persisted on the participant-day link so it survives the chip being re-read. The override wins
    /// over the status computed from the read-out. A no-op when the participant is not a member that day.
    /// </summary>
    Task SetParticipantDayResultStatusAsync(Guid participantId, Guid dayId, FinishStatus? status, CancellationToken cancellationToken = default);

    /// <summary>
    /// Computes the run results for one day from the finish read-outs, keyed by participant id. Used by
    /// the participant tables to refresh the result columns (and re-ranked places) in-memory after a
    /// status edit, without reloading the whole grid. Empty when no competition is selected.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, ParticipantDayResult>> GetDayResultsByParticipantAsync(Guid dayId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads the current day's finish-read log (ordered by sequence), each row resolved against the
    /// day's participants so a known chip carries its holder's number, full name and group. Returns an
    /// empty list when no day is selected.
    /// </summary>
    Task<IReadOnlyList<FinishReadoutRow>> GetFinishReadoutRowsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// True when the current day scores points — its default discipline is point-scoring (rogaine), or
    /// any group on the day overrides to one. Drives the finish-read log's optional «Бали» column. False
    /// when no day is selected.
    /// </summary>
    Task<bool> CurrentDayUsesScoreAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Builds the passage/splits view for one logged read-out (by its id) on the current day, comparing
    /// what the chip punched against the holder's prescribed course. The shape is discipline-specific
    /// (ordered for a set course, scored for the free-choice formats — see <see cref="SplitsView"/>).
    /// Returns null when no day is selected, the readout is unknown, or its chip is not held by anyone
    /// on the day (an unrecognised read has no course to compare against).
    /// </summary>
    Task<SplitsView?> GetFinishSplitsAsync(Guid readoutId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Builds a ready-to-print split printout for one logged read-out (by its id) on the current day: the
    /// runner's header (name, number, chip, group, result, status) plus the course passage in order,
    /// wrapping the same <see cref="SplitsView"/> the panel shows. Used by the finish-read print action.
    /// Returns null when no day is selected or the readout is unknown; an unrecognised chip still prints
    /// its raw passage (no holder header).
    /// </summary>
    Task<SplitPrintDocument?> GetSplitPrintDocumentAsync(Guid readoutId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Imports parsed read-out records into the current day's finish log. Append-only: each record that
    /// is not already logged (by content — chip + start/finish + punches) is added; identical records
    /// already present are skipped, so re-reading the same file never doubles rows. Duplicates of a chip
    /// with different content are kept. A no-op when no day is selected.
    /// </summary>
    Task<FinishReadoutImportResult> ImportFinishReadoutsAsync(ChipReadData data, CancellationToken cancellationToken = default);

    /// <summary>Clears the current day's finish-read log. Returns how many rows were removed.</summary>
    Task<int> ClearFinishReadoutsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads everything the finish-read edit modal needs for one logged read-out (by its id) on the
    /// current day: its current chip, start/finish times, punches and effective status, plus the day's
    /// participants the chip can be reassigned to (with the current holder, when any, pre-selected).
    /// Returns null when no day is selected or the read-out is unknown.
    /// </summary>
    Task<FinishReadoutEditData?> GetFinishReadoutEditAsync(Guid readoutId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves an edited read-out: its chip, start/finish times, punches and manual status override, and —
    /// when <see cref="FinishReadoutEdit.ReassignToParticipantId"/> is set — (re)assigns the read-out's
    /// chip to that participant on the current day, taking it from any previous holder. A no-op when no
    /// day is selected or the read-out is unknown.
    /// </summary>
    Task UpdateFinishReadoutAsync(FinishReadoutEdit edit, CancellationToken cancellationToken = default);

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

    /// <summary>
    /// Imports a parsed UOF participant file into the current competition. Each athlete becomes a
    /// competition-level participant with a per-day link (<c>ParticipantDay</c>) for every day their
    /// <c>ProgEvent</c> lists; days that do not yet exist are created. Regions, clubs, sports schools
    /// (ДЮСШ) and groups referenced by the file are created on demand (case-insensitive), and groups
    /// are attached to the days their members run. The competition organiser is set from the file's
    /// <c>&lt;Orgs&gt;</c> value when present.
    ///
    /// When <paramref name="clearFirst"/> is true the participant database (participants + all day
    /// links) is wiped before importing, so the file becomes the full roster. When false, an athlete
    /// matching an existing one by non-blank FOU code is updated in place (idempotent re-import);
    /// others are added.
    /// </summary>
    Task<ParticipantImportResult> ImportParticipantsAsync(
        UofParticipantData data,
        bool clearFirst,
        IProgress<ImportProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>Loads the current competition's chip-price overrides (note → price/day), ordered by note.</summary>
    Task<IReadOnlyList<ChipPriceOverride>> GetChipPriceOverridesAsync(CancellationToken cancellationToken = default);

    /// <summary>Appends a new, blank chip-price override to the current competition and returns it.</summary>
    Task<ChipPriceOverride> AddChipPriceOverrideRowAsync(CancellationToken cancellationToken = default);

    /// <summary>Saves an edited chip-price override (note, price).</summary>
    Task UpdateChipPriceOverrideAsync(ChipPriceOverride priceOverride, CancellationToken cancellationToken = default);

    /// <summary>Removes a chip-price override from the current competition.</summary>
    Task DeleteChipPriceOverrideAsync(Guid overrideId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets a participant's "pays the raised (late) fee" flag (competition-level, independent of any
    /// day). Affects the participant's computed total entry fee.
    /// </summary>
    Task SetParticipantPaysRaisedFeeAsync(Guid participantId, bool paysRaisedFee, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks (or unmarks) that a participant gets a particular entry-fee discount. Idempotent. The
    /// FSOU-member discount is auto-applied from <see cref="Participant.IsFsouMember"/> and must not be
    /// toggled through here.
    /// </summary>
    Task SetParticipantDiscountAsync(Guid participantId, Guid discountId, bool on, CancellationToken cancellationToken = default);

    /// <summary>Loads the current competition's entry-fee discounts; the FSOU-member discount sorts first, the rest by name.</summary>
    Task<IReadOnlyList<EntryFeeDiscount>> GetEntryFeeDiscountsAsync(CancellationToken cancellationToken = default);

    /// <summary>Appends a new, blank entry-fee discount to the current competition and returns it.</summary>
    Task<EntryFeeDiscount> AddEntryFeeDiscountRowAsync(CancellationToken cancellationToken = default);

    /// <summary>Saves an edited entry-fee discount (name, percent, applies-to-chip-rental).</summary>
    Task UpdateEntryFeeDiscountAsync(EntryFeeDiscount discount, CancellationToken cancellationToken = default);

    /// <summary>Removes an entry-fee discount from the current competition.</summary>
    Task DeleteEntryFeeDiscountAsync(Guid discountId, CancellationToken cancellationToken = default);
}

/// <summary>Outcome of a participant import, for reporting back to the user.</summary>
/// <param name="Added">How many participants were newly created.</param>
/// <param name="Updated">How many existing participants were matched by FOU code and updated.</param>
/// <param name="DaysCreated">How many days were created to satisfy referenced ProgEvent numbers.</param>
public readonly record struct ParticipantImportResult(int Added, int Updated, int DaysCreated);

/// <summary>Outcome of a bulk rental-chip range add, for reporting back to the user.</summary>
/// <param name="Added">How many chips were newly added.</param>
/// <param name="Skipped">How many requested numbers already existed and were skipped.</param>
public readonly record struct RentalChipBulkResult(int Added, int Skipped);

/// <summary>Outcome of a rental-chip file import, for reporting back to the user.</summary>
/// <param name="Added">How many chips were newly added.</param>
/// <param name="Skipped">How many distinct numbers in the file already existed and were skipped.</param>
public readonly record struct RentalChipImportResult(int Added, int Skipped);

/// <summary>Outcome of a finish read-out import, for reporting back to the user.</summary>
/// <param name="Added">How many read-out rows were newly logged.</param>
/// <param name="Skipped">How many records were already logged (identical content) and were skipped.</param>
public readonly record struct FinishReadoutImportResult(int Added, int Skipped);

/// <summary>Outcome of a group import, for reporting back to the user.</summary>
/// <param name="Added">How many groups were newly attached to the day.</param>
/// <param name="Updated">How many existing groups on the day were updated (update mode only).</param>
public readonly record struct GroupImportResult(int Added, int Updated);

/// <summary>Outcome of a control-point import, for reporting back to the user.</summary>
/// <param name="Imported">How many control points ended up in the day after the import.</param>
/// <param name="Added">How many points were newly added (in add-only mode).</param>
/// <param name="Replaced">True when the whole set was replaced.</param>
public readonly record struct ControlPointImportResult(int Imported, int Added, bool Replaced);
