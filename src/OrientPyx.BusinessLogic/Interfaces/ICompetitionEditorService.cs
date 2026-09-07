using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Interfaces;

/// <summary>
/// Reads and edits the current competition's metadata and days, operating on the event
/// selected in <see cref="ISessionService"/>. Keeps event-folder paths and EF Core out of
/// the presentation layer.
/// With no competition (or day) selected every member below is inert: reads return an empty
/// result or null, writes are a no-op — as is a per-day write for a participant who does not run
/// that day. Only deviations from that are documented.
/// </summary>
public interface ICompetitionEditorService
{
    Task<CompetitionInfo?> GetInfoAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Builds the dashboard overview for the selected competition and current day: competition summary,
    /// current-day info, and live counts (participants, groups, rental chips, finish read-outs and run
    /// results — finished / on-course). Returns a snapshot with <see cref="DashboardInfo.HasSelection"/>
    /// false when no competition is selected.
    /// </summary>
    Task<DashboardInfo> GetDashboardAsync(CancellationToken cancellationToken = default);

    Task SaveInfoAsync(CompetitionInfo info, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether the participants page offers the roster («Мандатка») view for this competition. True
    /// (the default) with no competition selected, so the page starts in its usual shape.
    /// </summary>
    Task<bool> GetRosterEnabledAsync(CancellationToken cancellationToken = default);

    /// <summary>Turns the roster view on or off, writing only that flag on the competition row.</summary>
    Task SetRosterEnabledAsync(bool enabled, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<EventDay>> GetDaysAsync(CancellationToken cancellationToken = default);

    /// <summary>Numbered after the last existing day.</summary>
    Task<EventDay> AddDayAsync(CancellationToken cancellationToken = default);

    Task UpdateDayAsync(EventDay day, CancellationToken cancellationToken = default);

    Task DeleteDayAsync(Guid dayId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens or closes the day for editing. A closed («закритий») day still reads, computes, prints
    /// and publishes normally — only writes to its own data are refused, so a finished day can't be
    /// changed by accident while working on another one. Returns the updated day, or null when it
    /// doesn't exist or already had that state.
    /// </summary>
    Task<EventDay?> SetDayLockedAsync(Guid dayId, bool locked, CancellationToken cancellationToken = default);

    /// <summary>
    /// Changes a day's 1-based number to <paramref name="newNumber"/> and renames its files folder
    /// (<c>day{old}</c> → <c>day{new}</c>) to match. The other days keep their numbers — this is a
    /// single-day re-label, not a re-ordering. Returns the updated day, or null when the change is
    /// rejected (number unchanged, out of range, or already used by another day).
    /// </summary>
    Task<EventDay?> ChangeDayNumberAsync(Guid dayId, int newNumber, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ControlPoint>> GetControlPointsAsync(CancellationToken cancellationToken = default);

    /// <summary>Appended to the current day.</summary>
    Task<ControlPoint> AddControlPointAsync(CancellationToken cancellationToken = default);

    Task UpdateControlPointAsync(ControlPoint point, CancellationToken cancellationToken = default);

    Task DeleteControlPointAsync(Guid pointId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks the current day's «проблемні КП»: every control whose id is in <paramref name="disabledPointIds"/>
    /// is flagged disabled, the rest are cleared. A disabled control is dropped from the prescribed/allowed
    /// course wherever it is required, so a runner missing it is not penalised (no MP) and a scored control no
    /// longer counts. Returns the number of control points whose flag changed.
    /// </summary>
    Task<int> SetProblematicControlsAsync(
        IReadOnlyCollection<Guid> disabledPointIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Imports the control points from a parsed IOF file into the current day. When
    /// <paramref name="replaceAll"/> is true the day's existing points are cleared and fully
    /// replaced; otherwise only codes not already present are appended (existing rows untouched).
    /// </summary>
    Task<ControlPointImportResult> ImportControlPointsAsync(
        IofCourseData data,
        bool replaceAll,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GroupDayRow>> GetGroupDayRowsAsync(CancellationToken cancellationToken = default);

    /// <summary>Loads a group's scatter («розсіювання») variants for the current day, in order (Code + raw
    /// course order). Empty when the group has none.</summary>
    Task<IReadOnlyList<ScatterVariantRow>> GetScatterVariantsAsync(Guid groupId, CancellationToken cancellationToken = default);

    /// <summary>Replaces a group's scatter variants for the current day with <paramref name="variants"/>
    /// (order = list order). An empty list clears them. Blank-code/blank-order rows are dropped by the service.</summary>
    Task SaveScatterVariantsAsync(Guid groupId, IReadOnlyList<ScatterVariantRow> variants, CancellationToken cancellationToken = default);

    /// <summary>Every group regardless of day, ordered by name.</summary>
    Task<IReadOnlyList<Group>> GetGroupsAsync(CancellationToken cancellationToken = default);

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
    /// Recomputes every competition group's birth-year window from its name and the competition start
    /// year (the same rule used at group creation, <see cref="Entities.Group.DeriveAgeWindow"/>), and
    /// overwrites the stored bounds. Unlike creation, this deliberately replaces existing windows — it
    /// is the user-triggered "recalculate age limits" action. Returns how many groups were updated.
    /// </summary>
    Task<int> RecalculateGroupAgeWindowsAsync(CancellationToken cancellationToken = default);

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

    /// <summary> Saves a file imported for the current day into that day's folder
    /// (<c>&lt;event&gt;/day{N}</c>), e.g. the IOF XML the courses came from. If a file with the same name and
    /// identical content is already there, nothing is written and the existing path is returned. If the name is
    /// taken by a file with different content, a short content hash is appended to keep both. </summary>
    Task<string?> SaveDayFileAsync(string fileName, byte[] content, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RentalChip>> GetRentalChipsAsync(CancellationToken cancellationToken = default);

    /// <summary>Blank; competition-level.</summary>
    Task<RentalChip> AddRentalChipAsync(CancellationToken cancellationToken = default);

    /// <summary>A change onto an existing number is ignored — numbers stay unique per competition.</summary>
    Task UpdateRentalChipAsync(RentalChip chip, CancellationToken cancellationToken = default);

    Task DeleteRentalChipAsync(Guid chipId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Maps each chip number that is assigned to one or more participants (on any day) to the
    /// comma-separated full names of those participants, deduplicated and in display order. Chips
    /// nobody holds are absent from the map. Keyed case-insensitively on the trimmed chip number, so a
    /// rental chip can look its holders up directly. Lets the rental-chip grid show who holds each chip.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> GetRentalChipHoldersAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns how many were deleted.</summary>
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

    Task<IReadOnlyList<Region>> GetRegionsAsync(CancellationToken cancellationToken = default);

    /// <summary>The Regions page "+ add" row.</summary>
    Task<Region> AddRegionRowAsync(CancellationToken cancellationToken = default);

    /// <summary>Reuses a same-name (case-insensitive) region or creates one. Blank name ⇒ null.</summary>
    Task<Region?> AddRegionAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>A rename onto an existing name is ignored — names stay unique per competition.</summary>
    Task UpdateRegionAsync(Region region, CancellationToken cancellationToken = default);

    /// <summary>Clears the region from any participant referencing it first.</summary>
    Task DeleteRegionAsync(Guid regionId, CancellationToken cancellationToken = default);

    /// <summary>Across the whole competition; regions with no participants map to 0.</summary>
    Task<IReadOnlyDictionary<Guid, int>> GetRegionParticipantCountsAsync(CancellationToken cancellationToken = default);

    /// <summary>Competition-level, independent of any day. Null clears it.</summary>
    Task SetParticipantRegionAsync(Guid participantId, Guid? regionId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Club>> GetClubsAsync(CancellationToken cancellationToken = default);

    /// <summary>The Clubs page "+ add" row.</summary>
    Task<Club> AddClubRowAsync(CancellationToken cancellationToken = default);

    /// <summary>Reuses a same-name (case-insensitive) club or creates one. Blank name ⇒ null.</summary>
    Task<Club?> AddClubAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>A rename onto an existing name is ignored — names stay unique per competition.</summary>
    Task UpdateClubAsync(Club club, CancellationToken cancellationToken = default);

    /// <summary>Clears the club from any participant referencing it first.</summary>
    Task DeleteClubAsync(Guid clubId, CancellationToken cancellationToken = default);

    /// <summary>Across the whole competition.</summary>
    Task<IReadOnlyDictionary<Guid, int>> GetClubParticipantCountsAsync(CancellationToken cancellationToken = default);

    /// <summary>Competition-level, independent of any day. Null clears it.</summary>
    Task SetParticipantClubAsync(Guid participantId, Guid? clubId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Dussh>> GetDusshesAsync(CancellationToken cancellationToken = default);

    /// <summary>The ДЮСШ page "+ add" row.</summary>
    Task<Dussh> AddDusshRowAsync(CancellationToken cancellationToken = default);

    /// <summary>Reuses a same-name (case-insensitive) school or creates one. Blank name ⇒ null.</summary>
    Task<Dussh?> AddDusshAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>A rename onto an existing name is ignored — names stay unique per competition.</summary>
    Task UpdateDusshAsync(Dussh dussh, CancellationToken cancellationToken = default);

    /// <summary>Clears the school from any participant referencing it first.</summary>
    Task DeleteDusshAsync(Guid dusshId, CancellationToken cancellationToken = default);

    /// <summary>Across the whole competition.</summary>
    Task<IReadOnlyDictionary<Guid, int>> GetDusshParticipantCountsAsync(CancellationToken cancellationToken = default);

    /// <summary>Competition-level, independent of any day. Null clears it.</summary>
    Task SetParticipantDusshAsync(Guid participantId, Guid? dusshId, CancellationToken cancellationToken = default);

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

    /// <summary> Creates a new, blank participant that does not yet run on any day (no day links). Used by the
    /// roster ("Мандатка") add: the participant appears as a non-member on every day and is assigned days by
    /// picking a group in the roster's per-day columns. Returns the new participant's roster row. </summary>
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
    /// Sets a participant's chip on a specific day (roster edit). A chip colliding with another participant on the same day is ignored (the
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
    /// Sets the start number (competition-level) on many participants at once, in a single transaction.
    /// Used by bulk number assignment so the whole batch persists together instead of through overlapping
    /// per-row autosaves. Each tuple is (participantId, number text). Returns how many were updated.
    /// </summary>
    Task<int> SetParticipantNumbersBatchAsync(
        IReadOnlyList<(Guid ParticipantId, string Number)> assignments,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// No uniqueness rule, so it is stored directly. A null time clears it.
    /// </summary>
    Task SetParticipantDayStartTimeAsync(Guid participantId, Guid dayId, TimeSpan? startTime, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets a participant's "out of competition" (поза конкурсом) flag on a specific day (roster edit).
    /// </summary>
    Task SetParticipantDayOutOfCompetitionAsync(Guid participantId, Guid dayId, bool outOfCompetition, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets (or clears, when null) a judge's manual finish-status override for a participant on a day,
    /// persisted on the participant-day link so it survives the chip being re-read. The override wins
    /// over the status computed from the read-out.
    /// </summary>
    Task SetParticipantDayResultStatusAsync(Guid participantId, Guid dayId, FinishStatus? status, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets (or clears, when null) a judge's «бонус» points correction for a participant on a point-scoring
    /// day, persisted on the participant-day link. Added to the computed «Бали»; may be positive or
    /// negative. For a rogaine team the correction applied to the team total is the smallest entered bonus
    /// among its members (see the scoring pass).
    /// </summary>
    Task SetParticipantDayBonusAsync(Guid participantId, Guid dayId, int? bonus, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets a participant's payment («Оплата») for one day, persisted on the participant-day link. Only
    /// meaningful while the competition is in per-day payment mode
    /// (<see cref="Entities.CompetitionInfo.PaymentPerDay"/>); the competition-level payment lives on the
    /// participant and is saved with the row. Has its own writer so the debounced row save can't wipe it.
    /// </summary>
    Task SetParticipantDayPaymentAsync(Guid participantId, Guid dayId, string payment, CancellationToken cancellationToken = default);

    /// <summary>
    /// Counts how many participants carry a non-blank payment value in the mode currently in force — the
    /// number a switch of <see cref="Entities.CompetitionInfo.PaymentPerDay"/> would redistribute. Lets the
    /// UI say «Зачеплено N учасників» before asking for confirmation.
    /// </summary>
    Task<int> CountParticipantsWithPaymentAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Switches the competition between "one payment for the whole competition" and "a payment per day",
    /// migrating every participant's stored value across in one transaction.
    ///
    /// Turning it ON spreads each participant's single payment over the days they run, in proportion to
    /// each day's share of their total fee (equal shares when the total is 0); the last day absorbs the
    /// rounding remainder so the parts add back up exactly. A value that is not a number goes to the first
    /// day as text. Turning it OFF sums the per-day numbers back into the single value (a non-numeric
    /// per-day value wins as text). Neither direction clears the source column, so switching back and forth
    /// never loses what the user typed. No-op when the mode already matches.
    /// </summary>
    Task SetPaymentPerDayAsync(bool paymentPerDay, CancellationToken cancellationToken = default);

    /// <summary>
    /// Computes the run results for one day from the finish read-outs, keyed by participant id. Used by
    /// the participant tables to refresh the result columns (and re-ranked places) in-memory after a
    /// status edit, without reloading the whole grid.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, ParticipantDayResult>> GetDayResultsByParticipantAsync(Guid dayId, CancellationToken cancellationToken = default);

    /// <summary> Gathers the results-protocol data for one day: every group that runs on the day, with its
    /// course metadata (length, control count, time limit) and its participant rows carrying the computed
    /// result. Groups follow the day grid order; rows are unsorted (the protocol builder orders them).
    /// </summary>
    Task<ResultProtocolData> GetResultProtocolDataAsync(Guid dayId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads a day's saved results-protocol template (orientation, ordered/visible columns, header text),
    /// or null when the day has no template yet — the caller then seeds it from the app-level default. The
    /// template is stored per day in the event database, so each day can have its own protocol layout.
    /// </summary>
    Task<ResultProtocolSettings?> GetResultProtocolSettingsAsync(Guid dayId, CancellationToken cancellationToken = default);

    Task SaveResultProtocolSettingsAsync(Guid dayId, ResultProtocolSettings settings, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gathers the participant-statement («відомість») data for the given participants: their identity fields
    /// plus the per-day membership fields (group, chip). When <paramref name="dayId"/> is set, the per-day
    /// fields come from that day; when it is null (roster scope), the distinct across-days values are joined
    /// with " / " (matching the roster's collapsed cells). Own vs rental chip and the chip sort key are
    /// resolved against the competition's rental-chip pool. Only the requested participants are returned (in no
    /// particular order — the builder sorts by chip).
    /// </summary>
    Task<StatementData> GetStatementDataAsync(Guid? dayId, IReadOnlyList<Guid> participantIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads the competition-level saved participant-statement template (orientation, ordered/visible columns,
    /// header text), or null when the competition has no template yet — the caller then seeds it from the
    /// app-level default.
    /// </summary>
    Task<StatementSettings?> GetStatementSettingsAsync(CancellationToken cancellationToken = default);

    Task SaveStatementSettingsAsync(StatementSettings settings, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gathers the start-protocol data for one day: every group that runs on the day with its members, each
    /// carrying its drawn start time (<see cref="ParticipantDay.StartTime"/>) and identity fields. Groups
    /// follow the day grid order; rows are unsorted (the start-protocol builder orders/groups them by kind).
    /// </summary>
    Task<StartProtocolData> GetStartProtocolDataAsync(Guid dayId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads a day's saved start-protocol template for a kind, or null when the (day, kind) has no template
    /// yet — the caller then seeds it from the kind's built-in default. Stored per day + kind in the event
    /// database.
    /// </summary>
    Task<StartProtocolSettings?> GetStartProtocolSettingsAsync(Guid dayId, StartProtocolKind kind, CancellationToken cancellationToken = default);

    Task SaveStartProtocolSettingsAsync(Guid dayId, StartProtocolKind kind, StartProtocolSettings settings, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads a day's saved start-draw settings for a draw page (the values last entered there), or null when
    /// the (day, kind) has nothing stored yet — the caller then keeps its defaults. Stored per day + kind in
    /// the event database.
    /// </summary>
    Task<DrawSettings?> GetDrawSettingsAsync(Guid dayId, DrawSettingsKind kind, CancellationToken cancellationToken = default);

    Task SaveDrawSettingsAsync(Guid dayId, DrawSettingsKind kind, DrawSettings settings, CancellationToken cancellationToken = default);

    /// <summary> Gathers the multi-day summary («Підсумковий залік») data: every competition day, and every
    /// group with its members and each member's per-day computed result. The summary builder aggregates and
    /// ranks. </summary>
    Task<SummaryProtocolData> GetSummaryProtocolDataAsync(CancellationToken cancellationToken = default);

    /// <summary>Null when none is stored yet.</summary>
    Task<SummaryProtocolSettings?> GetSummaryProtocolSettingsAsync(CancellationToken cancellationToken = default);

    Task SaveSummaryProtocolSettingsAsync(SummaryProtocolSettings settings, CancellationToken cancellationToken = default);

    /// <summary> Gathers the split-export data for one day: every group that runs on the day, with its course
    /// metadata, discipline layout (ordered set-course vs scored) and its participant rows — each carrying the
    /// discipline-built <see cref="SplitsView"/> (the passage/splits) and the computed result. Only members
    /// whose chip was read on the day are included (no read-out ⇒ no splits to show). Groups follow the day
    /// grid order; rows are unsorted (the builder orders them placed-first). </summary>
    Task<SplitExportData> GetDaySplitsExportDataAsync(Guid dayId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gathers the start-draw (жеребкування) preparation data for one day: every group that runs on the
    /// day with its members and the first control point of its course, plus each member's region, club and
    /// team (the attributes the draw can keep apart). Members carry their participant-day link id so the
    /// drawn start time can be written straight back.
    /// </summary>
    Task<DrawPrepData> GetDrawPrepDataAsync(Guid dayId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the drawn start times back onto the day's participant-day links in one batch. Each
    /// assignment is (link id → start time); a missing link is skipped. Returns how many links were
    /// updated.
    /// </summary>
    Task<int> SaveDrawStartTimesAsync(IReadOnlyList<DrawStartAssignment> assignments, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gathers the data for the manual start-order editor on one day: every group that runs on the day with
    /// its members carrying their current start time (and display fields), ordered by start time. The editor
    /// re-orders members within a group and re-hands out the same set of start minutes in the new order.
    /// </summary>
    Task<StartOrderData> GetStartOrderDataAsync(Guid dayId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gathers the data for the «Швидке зняття» (quick withdrawal) editor on one day: every participant on
    /// the day keyed by start number, carrying whether their chip has already been read (which forbids DNS)
    /// and their current manual status override. The editor resolves a typed number to a competitor and sets
    /// a status override on each.
    /// </summary>
    Task<QuickWithdrawalData> GetQuickWithdrawalDataAsync(Guid dayId, CancellationToken cancellationToken = default);

    /// <summary> Loads the current day's finish-read log (ordered by sequence), each row resolved against the
    /// day's participants so a known chip carries its holder's number, full name and group. </summary>
    Task<IReadOnlyList<FinishReadoutRow>> GetFinishReadoutRowsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// True when the current day scores points — its default discipline is point-scoring (rogaine), or
    /// any group on the day overrides to one. Drives the finish-read log's optional «Бали» column.
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

    /// <summary> Imports parsed read-out records into the current day's finish log. Append-only: each record
    /// that is not already logged (by content — chip + start/finish + punches) is added; identical records
    /// already present are skipped, so re-reading the same file never doubles rows. Duplicates of a chip with
    /// different content are kept. Skipped records are still reported back (with the id of the row they
    /// duplicate) so the caller can act on a re-read — e.g. print the slip again — without a second row. </summary>
    Task<FinishReadoutImportResult> ImportFinishReadoutsAsync(ChipReadData data, CancellationToken cancellationToken = default);

    /// <summary>Returns how many rows were removed.</summary>
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
    /// the reassignment with the user. Returns
    /// the participant id whose chip was cleared (the previous holder), or null when none.
    /// </summary>
    Task<Guid?> ReassignParticipantDayChipAsync(Guid participantId, Guid dayId, string chip, CancellationToken cancellationToken = default);

    /// <summary>
    /// Swaps two participants' chips on one day: <paramref name="participantId"/> takes
    /// <paramref name="chip"/> and the participant who held it gets <paramref name="previousChip"/>
    /// (the chip the first one had). Only meaningful when both are members of the day and someone else
    /// really holds the chip; a no-op otherwise. Returns the other participant's id when the swap
    /// happened, null when it did not.
    /// </summary>
    Task<Guid?> SwapParticipantDayChipsAsync(Guid participantId, Guid dayId, string chip, string previousChip, CancellationToken cancellationToken = default);

    /// <summary>
    /// Toggles a chip number's presence in the rental-chip database: adds it when absent, removes it
    /// when present (matched case-insensitively on the trimmed number). Returns true when the chip is
    /// now in the database (was added), false when it was removed (or the number was blank).
    /// </summary>
    Task<bool> ToggleRentalChipAsync(string number, CancellationToken cancellationToken = default);

    /// <summary>id + name, for the in-cell group dropdown.</summary>
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
    ///
    /// <paramref name="scope"/> selects how rows map to days. The default
    /// (<see cref="ParticipantImportScope.AllDays"/>) uses the file's day info. In
    /// <see cref="ParticipantImportMode.CurrentDayOnly"/> the file's day info is ignored and every
    /// athlete is entered on the active session day, matched to existing participants by the scope's
    /// link field, and <paramref name="clearFirst"/> then clears only that day's links (not the whole
    /// roster).
    /// </summary>
    Task<ParticipantImportResult> ImportParticipantsAsync(
        UofParticipantData data,
        bool clearFirst,
        ParticipantImportScope scope,
        IProgress<ImportProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary> Loads the current competition's online-publish settings (slug, displayed title/subtitle,
    /// standings / points flags, enabled). When the competition has no row yet, returns defaults seeded from
    /// its metadata (slug from identifier, title from name, subtitle from the date range). </summary>
    Task<OnlinePublishSettings?> GetOnlinePublishSettingsAsync(CancellationToken cancellationToken = default);

    Task SaveOnlinePublishSettingsAsync(OnlinePublishSettings settings, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gathers one publish tick's data for the online live-results service: the competition's day list (for the
    /// spectator day switcher) plus the given day's groups and computed result rows — built from the same
    /// computed results the protocols use. Returns <see cref="OnlineResultsSnapshot.Empty"/> when no competition
    /// is selected.
    /// </summary>
    Task<OnlineResultsSnapshot> GetOnlineResultsSnapshotAsync(Guid dayId, CancellationToken cancellationToken = default);

    /// <summary> Loads the current competition's results-monitor settings (the list of output HTML files with
    /// their group selection, columns and timing). Returns <see cref="MonitorSettings.Empty"/> when no row is
    /// stored yet. </summary>
    Task<MonitorSettings?> GetMonitorSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>Saves the current competition's results-monitor settings.</summary>
    Task SaveMonitorSettingsAsync(MonitorSettings settings, CancellationToken cancellationToken = default);

    /// <summary>
    /// The absolute output directory the monitor HTML files are written into — the active session day's folder
    /// (<c>events/&lt;id&gt;/day{N}</c>), so the screens live with the day whose results they show. Monitor
    /// files are addressed by file name only; this resolves where they live so the page can both write and open
    /// them. Returns null when no competition or day is selected.
    /// </summary>
    string? GetMonitorOutputDirectory();

    /// <summary>Resolves a monitor file name to its absolute path under <see cref="GetMonitorOutputDirectory"/>
    /// (the active day's folder). Returns null when no competition/day is selected or the name is blank.</summary>
    string? ResolveMonitorFilePath(string fileName);

    /// <summary>
    /// Builds the renderable monitor documents for the given day — one per active output file — from the day's
    /// computed results: each file's chosen groups (in day order), filtered to its selected columns and
    /// already-formatted. Pairs each document with its target file path so the caller can write it. Returns an
    /// empty list when no competition/day is selected or no file is configured.
    /// </summary>
    Task<IReadOnlyList<MonitorFileDocument>> BuildMonitorDocumentsAsync(
        Guid dayId, MonitorLabels labels, CancellationToken cancellationToken = default);

    /// <summary>
    /// Builds the renderable monitor document for a <b>single, possibly-unsaved</b> file — its chosen groups
    /// (in day order) filtered to the shared <paramref name="columns"/> selection, formatted from the day's
    /// computed results. Used by the configuration page's live preview, so reordering / hiding a column or
    /// toggling a group re-renders without saving first.
    /// </summary>
    Task<MonitorDocument?> BuildMonitorPreviewAsync(
        Guid dayId, MonitorFile file, ResultColumnSelection columns, MonitorLabels labels,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads (off the UI thread, once) the day's computed result snapshot the monitor preview builds from, so
    /// the page can cache it and re-render the preview on a column/group toggle <b>without</b> a DB round-trip
    /// (see <see cref="BuildMonitorPreview"/>).
    /// </summary>
    Task<MonitorPreviewSource?> GetMonitorPreviewSourceAsync(Guid dayId, CancellationToken cancellationToken = default);

    /// <summary>Synchronously builds one monitor document from a file + the shared <paramref name="columns"/>
    /// selection + a cached <see cref="MonitorPreviewSource"/> (no I/O), for instant live-preview re-renders as
    /// the user edits the shared columns or a file's groups.</summary>
    MonitorDocument BuildMonitorPreview(
        MonitorFile file, ResultColumnSelection columns, MonitorPreviewSource source, MonitorLabels labels);

    /// <summary>Loads the current competition's chip-price overrides (note → price/day), ordered by note.</summary>
    Task<IReadOnlyList<ChipPriceOverride>> GetChipPriceOverridesAsync(CancellationToken cancellationToken = default);

    /// <summary>Appends a new, blank chip-price override to the current competition and returns it.</summary>
    Task<ChipPriceOverride> AddChipPriceOverrideRowAsync(CancellationToken cancellationToken = default);

    /// <summary>Saves an edited chip-price override (note, price).</summary>
    Task UpdateChipPriceOverrideAsync(ChipPriceOverride priceOverride, CancellationToken cancellationToken = default);

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
/// <param name="AddedIds">Ids of the newly-logged rows, in log order — used to auto-print just the new reads.</param>
/// <param name="Duplicates">The skipped records, each pointing at the already-logged row it repeats. A caller
/// watching a file that is re-read whole every tick must decide for itself which of these are genuinely new
/// re-reads (see <see cref="FinishReadoutDuplicate"/>).</param>
public readonly record struct FinishReadoutImportResult(
    int Added,
    int Skipped,
    IReadOnlyList<Guid> AddedIds,
    IReadOnlyList<FinishReadoutDuplicate> Duplicates)
{
    public FinishReadoutImportResult(int Added, int Skipped) : this(Added, Skipped, [], []) { }
}

/// <summary>A read-out record that was already logged, so nothing was written for it.</summary>
/// <param name="ReadoutId">The existing log row this record is identical to.</param>
/// <param name="ContentKey">The record's content signature — stable across re-reads of the same physical
/// row, so a caller can tell a row it has already seen in the file from one that just appeared.</param>
/// <param name="ReadMark">The moment the chip was read out, raw from the file ("Read on" / "read at"), or
/// empty when the format carries none. A runner who reads out a second time produces the same
/// <paramref name="ContentKey"/> — same start, finish and punches — but a NEW mark, so the pair
/// (key, mark) identifies one physical read-out.</param>
public readonly record struct FinishReadoutDuplicate(Guid ReadoutId, string ContentKey, string ReadMark);

/// <summary>Outcome of a group import, for reporting back to the user.</summary>
/// <param name="Added">How many groups were newly attached to the day.</param>
/// <param name="Updated">How many existing groups on the day were updated (update mode only).</param>
/// <param name="ScatterFallbackGroups">
/// Names of groups that, on a Scatter («розсіювання») day, imported a course with only a single valid
/// running order and so were forced to «Заданий напрямок» (SetCourse) instead of inheriting the day's
/// Scatter default. Empty when the day is not Scatter or every group had genuine variants.
/// </param>
public readonly record struct GroupImportResult(
    int Added, int Updated, IReadOnlyList<string> ScatterFallbackGroups)
{
    public GroupImportResult(int Added, int Updated) : this(Added, Updated, []) { }
}

/// <summary>Outcome of a control-point import, for reporting back to the user.</summary>
/// <param name="Imported">How many control points ended up in the day after the import.</param>
/// <param name="Added">How many points were newly added (in add-only mode).</param>
/// <param name="Replaced">True when the whole set was replaced.</param>
public readonly record struct ControlPointImportResult(int Imported, int Added, bool Replaced);
