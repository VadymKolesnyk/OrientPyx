using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Interfaces;

/// <summary>
/// Abstraction over a single competition's database, addressed by its folder path.
/// Implemented in DataAccess; keeps EF Core out of BusinessLogic.
/// Every Update*/Delete* below is a no-op when the target row is missing.
/// </summary>
public interface IEventStore
{
    /// <summary>Creates the event database and schema for a folder if it does not exist.</summary>
    Task EnsureCreatedAsync(string eventFolderPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Merges the write-ahead log back into the main database file so the <c>event.db</c> on disk is
    /// self-contained (no pending data left only in the <c>-wal</c> sidecar). Used before exporting a
    /// competition so the archived database includes the latest committed changes even if the live
    /// connection is still open.
    /// </summary>
    Task CheckpointAsync(string eventFolderPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checkpoints the write-ahead log and releases every pooled connection to this competition's
    /// database, so its files are no longer held open by this process. Needed before the competition
    /// folder is moved or renamed — Windows refuses to rename a folder holding an open file.
    /// </summary>
    Task ReleaseAsync(string eventFolderPath, CancellationToken cancellationToken = default);

    Task<CompetitionInfo?> GetCompetitionInfoAsync(string eventFolderPath, CancellationToken cancellationToken = default);

    Task SaveCompetitionInfoAsync(string eventFolderPath, CompetitionInfo info, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets only the "hidden from the selection list" flag on the competition metadata row, without
    /// touching any other field (so it is safe to toggle from the picker without a full round-trip).
    /// </summary>
    Task SetHiddenAsync(string eventFolderPath, bool hidden, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets only the "participants page offers the roster view" flag on the competition metadata row,
    /// leaving every other field alone (same narrow-write reasoning as <see cref="SetHiddenAsync"/>).
    /// </summary>
    Task SetRosterEnabledAsync(string eventFolderPath, bool enabled, CancellationToken cancellationToken = default);

    /// <summary>Ordered by number.</summary>
    Task<IReadOnlyList<EventDay>> GetDaysAsync(string eventFolderPath, CancellationToken cancellationToken = default);

    Task AddDayAsync(string eventFolderPath, EventDay day, CancellationToken cancellationToken = default);

    Task UpdateDayAsync(string eventFolderPath, EventDay day, CancellationToken cancellationToken = default);

    /// <summary>Sets a day's 1-based number.</summary>
    Task UpdateDayNumberAsync(string eventFolderPath, Guid dayId, int newNumber, CancellationToken cancellationToken = default);

    Task DeleteDayAsync(string eventFolderPath, Guid dayId, CancellationToken cancellationToken = default);

    /// <summary>Ordered by sort order.</summary>
    Task<IReadOnlyList<ControlPoint>> GetControlPointsAsync(string eventFolderPath, Guid dayId, CancellationToken cancellationToken = default);

    Task AddControlPointAsync(string eventFolderPath, ControlPoint point, CancellationToken cancellationToken = default);

    /// <summary>Adds several control points to a day in one transaction (e.g. an XML import).</summary>
    Task AddControlPointsAsync(string eventFolderPath, IReadOnlyList<ControlPoint> points, CancellationToken cancellationToken = default);

    /// <summary>Deletes a day's existing control points and inserts the supplied set in one transaction.</summary>
    Task ReplaceControlPointsAsync(string eventFolderPath, Guid dayId, IReadOnlyList<ControlPoint> points, CancellationToken cancellationToken = default);

    Task UpdateControlPointAsync(string eventFolderPath, ControlPoint point, CancellationToken cancellationToken = default);

    Task DeleteControlPointAsync(string eventFolderPath, Guid pointId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the disabled («проблемний КП») flag across a day's control points: every point whose id is in
    /// <paramref name="disabledPointIds"/> is flagged disabled, the rest cleared, in one transaction.
    /// Returns the number of points whose flag actually changed.
    /// </summary>
    Task<int> SetControlPointsDisabledAsync(
        string eventFolderPath, Guid dayId, IReadOnlyCollection<Guid> disabledPointIds,
        CancellationToken cancellationToken = default);

    /// <summary>Ordered by name.</summary>
    Task<IReadOnlyList<Group>> GetGroupsAsync(string eventFolderPath, CancellationToken cancellationToken = default);

    Task AddGroupAsync(string eventFolderPath, Group group, CancellationToken cancellationToken = default);

    Task UpdateGroupAsync(string eventFolderPath, Group group, CancellationToken cancellationToken = default);

    Task DeleteGroupAsync(string eventFolderPath, Guid groupId, CancellationToken cancellationToken = default);

    /// <summary>Entry fee is shared across all days.</summary>
    Task UpdateGroupEntryFeeAsync(string eventFolderPath, Guid groupId, decimal? entryFee, CancellationToken cancellationToken = default);

    /// <summary>Both bounds inclusive, either optional; shared across days.</summary>
    Task UpdateGroupAgeWindowAsync(string eventFolderPath, Guid groupId, int? minBirthYear, int? maxBirthYear, CancellationToken cancellationToken = default);

    /// <summary>Ordered by sort order.</summary>
    Task<IReadOnlyList<GroupDaySettings>> GetGroupDaySettingsAsync(string eventFolderPath, Guid dayId, CancellationToken cancellationToken = default);

    /// <summary>Across all days — callers use this to decide cascade deletion.</summary>
    Task<int> CountGroupDaySettingsForGroupAsync(string eventFolderPath, Guid groupId, CancellationToken cancellationToken = default);

    Task AddGroupDaySettingsAsync(string eventFolderPath, GroupDaySettings settings, CancellationToken cancellationToken = default);

    /// <summary>Adds several group-day settings rows in one transaction (e.g. "pull all groups").</summary>
    Task AddGroupDaySettingsRangeAsync(string eventFolderPath, IReadOnlyList<GroupDaySettings> settings, CancellationToken cancellationToken = default);

    Task UpdateGroupDaySettingsAsync(string eventFolderPath, GroupDaySettings settings, CancellationToken cancellationToken = default);

    Task DeleteGroupDaySettingsAsync(string eventFolderPath, Guid settingsId, CancellationToken cancellationToken = default);

    /// <summary>Returns all scatter («розсіювання») variant rows for a day (across every group), ordered by
    /// their sort order. Empty when the day has no scatter groups.</summary>
    Task<IReadOnlyList<ScatterVariant>> GetScatterVariantsAsync(string eventFolderPath, Guid dayId, CancellationToken cancellationToken = default);

    /// <summary>Replaces a group's scatter variants for a day: deletes the existing (day, group) rows and
    /// inserts <paramref name="variants"/> in one transaction. An empty list just clears them (e.g. when the
    /// group stops being a scatter course).</summary>
    Task ReplaceScatterVariantsForGroupAsync(string eventFolderPath, Guid dayId, Guid groupId, IReadOnlyList<ScatterVariant> variants, CancellationToken cancellationToken = default);

    /// <summary>Ordered by number.</summary>
    Task<IReadOnlyList<RentalChip>> GetRentalChipsAsync(string eventFolderPath, CancellationToken cancellationToken = default);

    Task AddRentalChipAsync(string eventFolderPath, RentalChip chip, CancellationToken cancellationToken = default);

    /// <summary>Adds several rental chips in one transaction (e.g. a bulk range or a file import).</summary>
    Task AddRentalChipsAsync(string eventFolderPath, IReadOnlyList<RentalChip> chips, CancellationToken cancellationToken = default);

    Task UpdateRentalChipAsync(string eventFolderPath, RentalChip chip, CancellationToken cancellationToken = default);

    /// <summary>Removes a rental chip by id. Does nothing if it is missing.</summary>
    Task DeleteRentalChipAsync(string eventFolderPath, Guid chipId, CancellationToken cancellationToken = default);

    /// <summary>Removes every rental chip from the competition. Returns how many were deleted.</summary>
    Task<int> DeleteAllRentalChipsAsync(string eventFolderPath, CancellationToken cancellationToken = default);

    /// <summary>Returns the competition's regions, ordered by name.</summary>
    Task<IReadOnlyList<Region>> GetRegionsAsync(string eventFolderPath, CancellationToken cancellationToken = default);

    Task AddRegionAsync(string eventFolderPath, Region region, CancellationToken cancellationToken = default);

    Task UpdateRegionAsync(string eventFolderPath, Region region, CancellationToken cancellationToken = default);

    Task DeleteRegionAsync(string eventFolderPath, Guid regionId, CancellationToken cancellationToken = default);

    /// <summary>Sets RegionId to null on every participant referencing it.</summary>
    Task ClearParticipantsRegionAsync(string eventFolderPath, Guid regionId, CancellationToken cancellationToken = default);

    /// <summary>Ordered by name.</summary>
    Task<IReadOnlyList<Club>> GetClubsAsync(string eventFolderPath, CancellationToken cancellationToken = default);

    Task AddClubAsync(string eventFolderPath, Club club, CancellationToken cancellationToken = default);

    Task UpdateClubAsync(string eventFolderPath, Club club, CancellationToken cancellationToken = default);

    Task DeleteClubAsync(string eventFolderPath, Guid clubId, CancellationToken cancellationToken = default);

    /// <summary>Sets ClubId to null on every participant referencing it.</summary>
    Task ClearParticipantsClubAsync(string eventFolderPath, Guid clubId, CancellationToken cancellationToken = default);

    /// <summary>Sports schools (ДЮСШ), ordered by name.</summary>
    Task<IReadOnlyList<Dussh>> GetDusshesAsync(string eventFolderPath, CancellationToken cancellationToken = default);

    Task AddDusshAsync(string eventFolderPath, Dussh dussh, CancellationToken cancellationToken = default);

    Task UpdateDusshAsync(string eventFolderPath, Dussh dussh, CancellationToken cancellationToken = default);

    Task DeleteDusshAsync(string eventFolderPath, Guid dusshId, CancellationToken cancellationToken = default);

    /// <summary>Sets DusshId to null on every participant referencing it.</summary>
    Task ClearParticipantsDusshAsync(string eventFolderPath, Guid dusshId, CancellationToken cancellationToken = default);

    /// <summary>Ordered by surname then name.</summary>
    Task<IReadOnlyList<Participant>> GetParticipantsAsync(string eventFolderPath, CancellationToken cancellationToken = default);

    Task AddParticipantAsync(string eventFolderPath, Participant participant, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes every participant and every participant-day link from the competition in one
    /// transaction (used by the participant import's "clear first" option). Returns how many
    /// participants were deleted.
    /// </summary>
    Task<int> DeleteAllParticipantsAsync(string eventFolderPath, CancellationToken cancellationToken = default);

    Task UpdateParticipantAsync(string eventFolderPath, Participant participant, CancellationToken cancellationToken = default);

    Task DeleteParticipantAsync(string eventFolderPath, Guid participantId, CancellationToken cancellationToken = default);

    Task SetParticipantPaysRaisedFeeAsync(string eventFolderPath, Guid participantId, bool paysRaisedFee, CancellationToken cancellationToken = default);

    /// <summary>Every participant↔discount link in the competition.</summary>
    Task<IReadOnlyList<ParticipantDiscount>> GetParticipantDiscountsAsync(string eventFolderPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds or removes the link marking that a participant gets a discount. Idempotent: turning a link
    /// on when it already exists (or off when it doesn't) is a no-op.
    /// </summary>
    Task SetParticipantDiscountAsync(string eventFolderPath, Guid participantId, Guid discountId, bool on, CancellationToken cancellationToken = default);

    /// <summary>Ordered by sort order.</summary>
    Task<IReadOnlyList<ParticipantDay>> GetParticipantDaysAsync(string eventFolderPath, Guid dayId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads every table a per-day view needs (links, participants, groups, day settings, control points,
    /// regions/clubs/ДЮСШ, days, competition info) inside ONE transaction, so the returned lists are all
    /// from the same consistent snapshot. Use this instead of issuing the reads one by one: separate reads
    /// each open their own connection and, under WAL, may land on different snapshots when a write happens
    /// in between — which mixes stale and fresh rows into one result.
    /// </summary>
    Task<EventDaySnapshot> GetDaySnapshotAsync(string eventFolderPath, Guid dayId, CancellationToken cancellationToken = default);

    /// <summary>Across all days — the roster («Мандатка») source.</summary>
    Task<IReadOnlyList<ParticipantDay>> GetAllParticipantDaysAsync(string eventFolderPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets a participant's group on a day, creating the link when the participant does not run that day
    /// yet, and returns the link's id. Look-up and insert happen in ONE transaction, so two concurrent
    /// callers cannot both decide the link is missing and each add one (which used to leave a duplicate
    /// that printed the runner twice on every protocol). An existing link keeps its chip/order/start time.
    /// </summary>
    Task<Guid> SetParticipantDayGroupAsync(string eventFolderPath, Guid participantId, Guid dayId, Guid? groupId, CancellationToken cancellationToken = default);

    /// <summary>Across all days — callers use this to decide cascade deletion.</summary>
    Task<int> CountParticipantDaysForParticipantAsync(string eventFolderPath, Guid participantId, CancellationToken cancellationToken = default);

    Task AddParticipantDayAsync(string eventFolderPath, ParticipantDay link, CancellationToken cancellationToken = default);

    /// <summary>Updates a participant-day link (group, chip, start, order, out-of-competition). Deliberately
    /// does NOT touch <see cref="ParticipantDay.ResultStatusOverride"/> — that judge override has its own
    /// writer (<see cref="SetParticipantDayResultStatusAsync"/>) so the debounced row save can't wipe it.
    /// Does nothing if the link is missing.</summary>
    Task UpdateParticipantDayAsync(string eventFolderPath, ParticipantDay link, CancellationToken cancellationToken = default);

    /// <summary>Writes only the result-status override on one participant-day link (the judge's manual
    /// status; null clears it back to the computed status). The sole writer of that column — kept separate
    /// from <see cref="UpdateParticipantDayAsync"/> so the row save never clobbers it. No-op if missing.</summary>
    Task SetParticipantDayResultStatusAsync(string eventFolderPath, Guid linkId, FinishStatus? status, CancellationToken cancellationToken = default);

    /// <summary>Writes only the points-correction «бонус» on one participant-day link (added to the computed
    /// «Бали»; may be positive or negative, null clears it). The sole writer of that column — kept separate
    /// from <see cref="UpdateParticipantDayAsync"/> so the debounced row save never clobbers it. No-op if
    /// the link is missing.</summary>
    Task SetParticipantDayBonusAsync(string eventFolderPath, Guid linkId, int? bonus, CancellationToken cancellationToken = default);

    /// <summary>Writes only the per-day payment («Оплата») on one participant-day link. The sole writer of
    /// that column — kept separate from <see cref="UpdateParticipantDayAsync"/> so the debounced row save
    /// never clobbers it. No-op if the link is missing.</summary>
    Task SetParticipantDayPaymentAsync(string eventFolderPath, Guid linkId, string payment, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes only <see cref="CompetitionInfo.PaymentPerDay"/> plus the migrated payment values, in a
    /// single transaction: the participant-level payments in <paramref name="participantPayments"/> and the
    /// per-day ones in <paramref name="dayPayments"/> (keyed by participant-day link id). Either map may be
    /// empty; missing rows are skipped. Kept as one store call so a half-migrated competition can't happen.
    /// </summary>
    Task SetPaymentPerDayAsync(
        string eventFolderPath,
        bool paymentPerDay,
        IReadOnlyDictionary<Guid, string> participantPayments,
        IReadOnlyDictionary<Guid, string> dayPayments,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the chip on many participant-day links at once, in a single transaction (used by bulk chip
    /// assignment). Each tuple is (participantId, dayId, chip); a missing link is skipped. One
    /// <see cref="DbContext.SaveChangesAsync"/> keeps it fast for a whole roster instead of one write
    /// per cell. Returns how many links were updated.
    /// </summary>
    Task<int> SetParticipantDayChipsBatchAsync(
        string eventFolderPath,
        IReadOnlyList<(Guid ParticipantId, Guid DayId, string Chip)> assignments,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the start time on many participant-day links at once, in a single transaction (used by the
    /// start draw). Each tuple is (link id, start time); a missing link is skipped. One
    /// <see cref="DbContext.SaveChangesAsync"/> keeps a whole day's draw fast. Returns how many links were
    /// updated.
    /// </summary>
    Task<int> SetParticipantDayStartTimesBatchAsync(
        string eventFolderPath,
        IReadOnlyList<(Guid LinkId, TimeSpan StartTime)> assignments,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the start number (<see cref="Entities.Participant.Number"/>, competition-level) on many
    /// participants at once, in a single transaction (used by bulk number assignment). Each tuple is
    /// (participantId, number text); a missing participant is skipped. One
    /// <see cref="DbContext.SaveChangesAsync"/> commits the whole assignment so nothing is lost to
    /// overlapping per-row autosaves. Returns how many participants were updated.
    /// </summary>
    Task<int> SetParticipantNumbersBatchAsync(
        string eventFolderPath,
        IReadOnlyList<(Guid ParticipantId, string Number)> assignments,
        CancellationToken cancellationToken = default);

    Task DeleteParticipantDayAsync(string eventFolderPath, Guid linkId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Imports a whole parsed UOF roster in a single database transaction. Resolves/creates the
    /// referenced regions, clubs, sports schools and groups, matches existing participants by FOU
    /// code (unless <paramref name="clearFirst"/> wipes the roster first), attaches groups to the
    /// days their members run, and writes one participant-day link per referenced day. The days must
    /// already exist — the caller creates any missing ones (with their folders) beforehand. Reports
    /// coarse progress through <paramref name="progress"/> as it works. One <see cref="SaveChangesAsync"/>
    /// at the end keeps it fast for large files (instead of a transaction per row).
    /// </summary>
    Task<ParticipantImportResult> ImportParticipantsBatchAsync(
        string eventFolderPath,
        UofParticipantData data,
        bool clearFirst,
        int daysCreated,
        ParticipantImportScope scope,
        IProgress<ImportProgress>? progress,
        CancellationToken cancellationToken = default);

    /// <summary>Ordered by Order (read sequence).</summary>
    Task<IReadOnlyList<FinishReadout>> GetFinishReadoutsAsync(string eventFolderPath, Guid dayId, CancellationToken cancellationToken = default);

    /// <summary>One transaction per auto-read tick.</summary>
    Task AddFinishReadoutsAsync(string eventFolderPath, IReadOnlyList<FinishReadout> readouts, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates one finish read-out's editable fields (chip, start/finish times, punches + punch times, and
    /// the manual status override). Used by the finish-read edit modal. Does nothing if the read-out is
    /// missing. The <see cref="FinishReadout.Order"/>, day and content key are left untouched.
    /// </summary>
    Task UpdateFinishReadoutAsync(string eventFolderPath, FinishReadout readout, CancellationToken cancellationToken = default);

    /// <summary>Returns how many were deleted.</summary>
    Task<int> DeleteFinishReadoutsForDayAsync(string eventFolderPath, Guid dayId, CancellationToken cancellationToken = default);

    /// <summary>note → price/day, ordered by note.</summary>
    Task<IReadOnlyList<ChipPriceOverride>> GetChipPriceOverridesAsync(string eventFolderPath, CancellationToken cancellationToken = default);

    Task AddChipPriceOverrideAsync(string eventFolderPath, ChipPriceOverride priceOverride, CancellationToken cancellationToken = default);

    Task UpdateChipPriceOverrideAsync(string eventFolderPath, ChipPriceOverride priceOverride, CancellationToken cancellationToken = default);

    Task DeleteChipPriceOverrideAsync(string eventFolderPath, Guid overrideId, CancellationToken cancellationToken = default);

    /// <summary>Ordered by name.</summary>
    Task<IReadOnlyList<EntryFeeDiscount>> GetEntryFeeDiscountsAsync(string eventFolderPath, CancellationToken cancellationToken = default);

    Task AddEntryFeeDiscountAsync(string eventFolderPath, EntryFeeDiscount discount, CancellationToken cancellationToken = default);

    Task UpdateEntryFeeDiscountAsync(string eventFolderPath, EntryFeeDiscount discount, CancellationToken cancellationToken = default);

    Task DeleteEntryFeeDiscountAsync(string eventFolderPath, Guid discountId, CancellationToken cancellationToken = default);

    /// <summary>Null when the day has no row yet — caller seeds from the app-level default.</summary>
    Task<string?> GetResultProtocolJsonAsync(string eventFolderPath, Guid dayId, CancellationToken cancellationToken = default);

    Task SaveResultProtocolJsonAsync(string eventFolderPath, Guid dayId, string json, CancellationToken cancellationToken = default);

    /// <summary>Null when the (day, kind) has no row yet — caller seeds from the kind's default.</summary>
    Task<string?> GetStartProtocolJsonAsync(string eventFolderPath, Guid dayId, StartProtocolKind kind, CancellationToken cancellationToken = default);

    Task SaveStartProtocolJsonAsync(string eventFolderPath, Guid dayId, StartProtocolKind kind, string json, CancellationToken cancellationToken = default);

    /// <summary>Null when the (day, kind) has no row yet — caller falls back to page defaults.</summary>
    Task<string?> GetDrawSettingsJsonAsync(string eventFolderPath, Guid dayId, DrawSettingsKind kind, CancellationToken cancellationToken = default);

    Task SaveDrawSettingsJsonAsync(string eventFolderPath, Guid dayId, DrawSettingsKind kind, string json, CancellationToken cancellationToken = default);

    /// <summary>Competition-level; null when none is stored.</summary>
    Task<string?> GetSummaryProtocolJsonAsync(string eventFolderPath, CancellationToken cancellationToken = default);

    Task SaveSummaryProtocolJsonAsync(string eventFolderPath, string json, CancellationToken cancellationToken = default);

    /// <summary>Participant statement («відомість»); null ⇒ caller seeds from the app-level default.</summary>
    Task<string?> GetStatementJsonAsync(string eventFolderPath, CancellationToken cancellationToken = default);

    Task SaveStatementJsonAsync(string eventFolderPath, string json, CancellationToken cancellationToken = default);

    /// <summary>Null when none is stored — caller seeds from the competition metadata.</summary>
    Task<string?> GetOnlinePublishJsonAsync(string eventFolderPath, CancellationToken cancellationToken = default);

    Task SaveOnlinePublishJsonAsync(string eventFolderPath, string json, CancellationToken cancellationToken = default);

    /// <summary>Competition-level; null when none is stored.</summary>
    Task<string?> GetMonitorJsonAsync(string eventFolderPath, CancellationToken cancellationToken = default);

    Task SaveMonitorJsonAsync(string eventFolderPath, string json, CancellationToken cancellationToken = default);
}
