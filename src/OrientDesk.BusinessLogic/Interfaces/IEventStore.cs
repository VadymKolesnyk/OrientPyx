using OrientDesk.BusinessLogic.Entities;
using OrientDesk.BusinessLogic.Enums;
using OrientDesk.BusinessLogic.Models;

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

    /// <summary>Sets a day's 1-based number. Does nothing if the day is missing.</summary>
    Task UpdateDayNumberAsync(string eventFolderPath, Guid dayId, int newNumber, CancellationToken cancellationToken = default);

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

    /// <summary>Returns the competition's groups, ordered by name.</summary>
    Task<IReadOnlyList<Group>> GetGroupsAsync(string eventFolderPath, CancellationToken cancellationToken = default);

    /// <summary>Adds a competition-level group.</summary>
    Task AddGroupAsync(string eventFolderPath, Group group, CancellationToken cancellationToken = default);

    /// <summary>Updates an existing group's editable fields (name). Does nothing if it is missing.</summary>
    Task UpdateGroupAsync(string eventFolderPath, Group group, CancellationToken cancellationToken = default);

    /// <summary>Removes a group by id. Does nothing if it is missing.</summary>
    Task DeleteGroupAsync(string eventFolderPath, Guid groupId, CancellationToken cancellationToken = default);

    /// <summary>Sets a group's base entry fee (shared across days). Does nothing if the group is missing.</summary>
    Task UpdateGroupEntryFeeAsync(string eventFolderPath, Guid groupId, decimal? entryFee, CancellationToken cancellationToken = default);

    /// <summary>Sets a group's allowed birth-year window (both bounds inclusive, either optional; shared
    /// across days). Does nothing if the group is missing.</summary>
    Task UpdateGroupAgeWindowAsync(string eventFolderPath, Guid groupId, int? minBirthYear, int? maxBirthYear, CancellationToken cancellationToken = default);

    /// <summary>Returns a day's group settings rows, ordered by their sort order.</summary>
    Task<IReadOnlyList<GroupDaySettings>> GetGroupDaySettingsAsync(string eventFolderPath, Guid dayId, CancellationToken cancellationToken = default);

    /// <summary>Counts a group's settings rows across all days (used to decide cascade deletion).</summary>
    Task<int> CountGroupDaySettingsForGroupAsync(string eventFolderPath, Guid groupId, CancellationToken cancellationToken = default);

    /// <summary>Adds a group-day settings row (attaches a group to a day).</summary>
    Task AddGroupDaySettingsAsync(string eventFolderPath, GroupDaySettings settings, CancellationToken cancellationToken = default);

    /// <summary>Adds several group-day settings rows in one transaction (e.g. "pull all groups").</summary>
    Task AddGroupDaySettingsRangeAsync(string eventFolderPath, IReadOnlyList<GroupDaySettings> settings, CancellationToken cancellationToken = default);

    /// <summary>Updates a group-day settings row (course order, distance, override). Does nothing if it is missing.</summary>
    Task UpdateGroupDaySettingsAsync(string eventFolderPath, GroupDaySettings settings, CancellationToken cancellationToken = default);

    /// <summary>Removes a group-day settings row by id (detaches a group from a day). Does nothing if it is missing.</summary>
    Task DeleteGroupDaySettingsAsync(string eventFolderPath, Guid settingsId, CancellationToken cancellationToken = default);

    /// <summary>Returns the competition's rental chips, ordered by number.</summary>
    Task<IReadOnlyList<RentalChip>> GetRentalChipsAsync(string eventFolderPath, CancellationToken cancellationToken = default);

    /// <summary>Adds a rental chip to the competition.</summary>
    Task AddRentalChipAsync(string eventFolderPath, RentalChip chip, CancellationToken cancellationToken = default);

    /// <summary>Adds several rental chips in one transaction (e.g. a bulk range or a file import).</summary>
    Task AddRentalChipsAsync(string eventFolderPath, IReadOnlyList<RentalChip> chips, CancellationToken cancellationToken = default);

    /// <summary>Updates an existing rental chip's editable fields (number, note). Does nothing if it is missing.</summary>
    Task UpdateRentalChipAsync(string eventFolderPath, RentalChip chip, CancellationToken cancellationToken = default);

    /// <summary>Removes a rental chip by id. Does nothing if it is missing.</summary>
    Task DeleteRentalChipAsync(string eventFolderPath, Guid chipId, CancellationToken cancellationToken = default);

    /// <summary>Removes every rental chip from the competition. Returns how many were deleted.</summary>
    Task<int> DeleteAllRentalChipsAsync(string eventFolderPath, CancellationToken cancellationToken = default);

    /// <summary>Returns the competition's regions, ordered by name.</summary>
    Task<IReadOnlyList<Region>> GetRegionsAsync(string eventFolderPath, CancellationToken cancellationToken = default);

    /// <summary>Adds a competition-level region.</summary>
    Task AddRegionAsync(string eventFolderPath, Region region, CancellationToken cancellationToken = default);

    /// <summary>Updates an existing region's editable fields (name). Does nothing if it is missing.</summary>
    Task UpdateRegionAsync(string eventFolderPath, Region region, CancellationToken cancellationToken = default);

    /// <summary>Removes a region by id. Does nothing if it is missing.</summary>
    Task DeleteRegionAsync(string eventFolderPath, Guid regionId, CancellationToken cancellationToken = default);

    /// <summary>Clears a region from every participant that references it (sets their RegionId to null).</summary>
    Task ClearParticipantsRegionAsync(string eventFolderPath, Guid regionId, CancellationToken cancellationToken = default);

    /// <summary>Returns the competition's clubs, ordered by name.</summary>
    Task<IReadOnlyList<Club>> GetClubsAsync(string eventFolderPath, CancellationToken cancellationToken = default);

    /// <summary>Adds a competition-level club.</summary>
    Task AddClubAsync(string eventFolderPath, Club club, CancellationToken cancellationToken = default);

    /// <summary>Updates an existing club's editable fields (name). Does nothing if it is missing.</summary>
    Task UpdateClubAsync(string eventFolderPath, Club club, CancellationToken cancellationToken = default);

    /// <summary>Removes a club by id. Does nothing if it is missing.</summary>
    Task DeleteClubAsync(string eventFolderPath, Guid clubId, CancellationToken cancellationToken = default);

    /// <summary>Clears a club from every participant that references it (sets their ClubId to null).</summary>
    Task ClearParticipantsClubAsync(string eventFolderPath, Guid clubId, CancellationToken cancellationToken = default);

    /// <summary>Returns the competition's sports schools (ДЮСШ), ordered by name.</summary>
    Task<IReadOnlyList<Dussh>> GetDusshesAsync(string eventFolderPath, CancellationToken cancellationToken = default);

    /// <summary>Adds a competition-level sports school.</summary>
    Task AddDusshAsync(string eventFolderPath, Dussh dussh, CancellationToken cancellationToken = default);

    /// <summary>Updates an existing sports school's editable fields (name). Does nothing if it is missing.</summary>
    Task UpdateDusshAsync(string eventFolderPath, Dussh dussh, CancellationToken cancellationToken = default);

    /// <summary>Removes a sports school by id. Does nothing if it is missing.</summary>
    Task DeleteDusshAsync(string eventFolderPath, Guid dusshId, CancellationToken cancellationToken = default);

    /// <summary>Clears a sports school from every participant that references it (sets their DusshId to null).</summary>
    Task ClearParticipantsDusshAsync(string eventFolderPath, Guid dusshId, CancellationToken cancellationToken = default);

    /// <summary>Returns the competition's participants, ordered by surname then name.</summary>
    Task<IReadOnlyList<Participant>> GetParticipantsAsync(string eventFolderPath, CancellationToken cancellationToken = default);

    /// <summary>Adds a competition-level participant.</summary>
    Task AddParticipantAsync(string eventFolderPath, Participant participant, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes every participant and every participant-day link from the competition in one
    /// transaction (used by the participant import's "clear first" option). Returns how many
    /// participants were deleted.
    /// </summary>
    Task<int> DeleteAllParticipantsAsync(string eventFolderPath, CancellationToken cancellationToken = default);

    /// <summary>Updates a participant's identity fields (surname, name, number, rank, coach, birth date). Does nothing if it is missing.</summary>
    Task UpdateParticipantAsync(string eventFolderPath, Participant participant, CancellationToken cancellationToken = default);

    /// <summary>Removes a participant by id. Does nothing if it is missing.</summary>
    Task DeleteParticipantAsync(string eventFolderPath, Guid participantId, CancellationToken cancellationToken = default);

    /// <summary>Sets a participant's "pays the raised fee" flag. Does nothing if it is missing.</summary>
    Task SetParticipantPaysRaisedFeeAsync(string eventFolderPath, Guid participantId, bool paysRaisedFee, CancellationToken cancellationToken = default);

    /// <summary>Returns every participant↔discount link in the competition (used to resolve selected discounts).</summary>
    Task<IReadOnlyList<ParticipantDiscount>> GetParticipantDiscountsAsync(string eventFolderPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds or removes the link marking that a participant gets a discount. Idempotent: turning a link
    /// on when it already exists (or off when it doesn't) is a no-op.
    /// </summary>
    Task SetParticipantDiscountAsync(string eventFolderPath, Guid participantId, Guid discountId, bool on, CancellationToken cancellationToken = default);

    /// <summary>Returns a day's participant links, ordered by their sort order.</summary>
    Task<IReadOnlyList<ParticipantDay>> GetParticipantDaysAsync(string eventFolderPath, Guid dayId, CancellationToken cancellationToken = default);

    /// <summary>Returns every participant link across all days (used to build the roster / Мандатка view).</summary>
    Task<IReadOnlyList<ParticipantDay>> GetAllParticipantDaysAsync(string eventFolderPath, CancellationToken cancellationToken = default);

    /// <summary>Counts a participant's links across all days (used to decide cascade deletion).</summary>
    Task<int> CountParticipantDaysForParticipantAsync(string eventFolderPath, Guid participantId, CancellationToken cancellationToken = default);

    /// <summary>Adds a participant-day link (attaches a participant to a day).</summary>
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

    /// <summary>Removes a participant-day link by id (detaches a participant from a day). Does nothing if it is missing.</summary>
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
        IProgress<ImportProgress>? progress,
        CancellationToken cancellationToken = default);

    /// <summary>Returns a day's finish read-outs, ordered by their sequence (Order).</summary>
    Task<IReadOnlyList<FinishReadout>> GetFinishReadoutsAsync(string eventFolderPath, Guid dayId, CancellationToken cancellationToken = default);

    /// <summary>Adds several finish read-outs to a day in one transaction (an auto-read tick).</summary>
    Task AddFinishReadoutsAsync(string eventFolderPath, IReadOnlyList<FinishReadout> readouts, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates one finish read-out's editable fields (chip, start/finish times, punches + punch times, and
    /// the manual status override). Used by the finish-read edit modal. Does nothing if the read-out is
    /// missing. The <see cref="FinishReadout.Order"/>, day and content key are left untouched.
    /// </summary>
    Task UpdateFinishReadoutAsync(string eventFolderPath, FinishReadout readout, CancellationToken cancellationToken = default);

    /// <summary>Removes every finish read-out from a day. Returns how many were deleted.</summary>
    Task<int> DeleteFinishReadoutsForDayAsync(string eventFolderPath, Guid dayId, CancellationToken cancellationToken = default);

    /// <summary>Returns the competition's chip-price overrides (note → price/day), ordered by note.</summary>
    Task<IReadOnlyList<ChipPriceOverride>> GetChipPriceOverridesAsync(string eventFolderPath, CancellationToken cancellationToken = default);

    /// <summary>Adds a chip-price override to the competition.</summary>
    Task AddChipPriceOverrideAsync(string eventFolderPath, ChipPriceOverride priceOverride, CancellationToken cancellationToken = default);

    /// <summary>Updates a chip-price override's editable fields (note, price). Does nothing if it is missing.</summary>
    Task UpdateChipPriceOverrideAsync(string eventFolderPath, ChipPriceOverride priceOverride, CancellationToken cancellationToken = default);

    /// <summary>Removes a chip-price override by id. Does nothing if it is missing.</summary>
    Task DeleteChipPriceOverrideAsync(string eventFolderPath, Guid overrideId, CancellationToken cancellationToken = default);

    /// <summary>Returns the competition's entry-fee discounts, ordered by name.</summary>
    Task<IReadOnlyList<EntryFeeDiscount>> GetEntryFeeDiscountsAsync(string eventFolderPath, CancellationToken cancellationToken = default);

    /// <summary>Adds an entry-fee discount to the competition.</summary>
    Task AddEntryFeeDiscountAsync(string eventFolderPath, EntryFeeDiscount discount, CancellationToken cancellationToken = default);

    /// <summary>Updates an entry-fee discount's editable fields (name, percent, applies-to-chip). Does nothing if it is missing.</summary>
    Task UpdateEntryFeeDiscountAsync(string eventFolderPath, EntryFeeDiscount discount, CancellationToken cancellationToken = default);

    /// <summary>Removes an entry-fee discount by id. Does nothing if it is missing.</summary>
    Task DeleteEntryFeeDiscountAsync(string eventFolderPath, Guid discountId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a day's saved results-protocol template JSON, or null when the day has no row yet (the
    /// caller then seeds it from the app-level default).
    /// </summary>
    Task<string?> GetResultProtocolJsonAsync(string eventFolderPath, Guid dayId, CancellationToken cancellationToken = default);

    /// <summary>Stores (inserts/updates) a day's results-protocol template JSON.</summary>
    Task SaveResultProtocolJsonAsync(string eventFolderPath, Guid dayId, string json, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a day's saved start-protocol template JSON for a kind, or null when the (day, kind) has no row
    /// yet (the caller then seeds it from the kind's built-in default).
    /// </summary>
    Task<string?> GetStartProtocolJsonAsync(string eventFolderPath, Guid dayId, StartProtocolKind kind, CancellationToken cancellationToken = default);

    /// <summary>Stores (inserts/updates) a day's start-protocol template JSON for a kind.</summary>
    Task SaveStartProtocolJsonAsync(string eventFolderPath, Guid dayId, StartProtocolKind kind, string json, CancellationToken cancellationToken = default);

    /// <summary>Returns the competition-level summary-protocol template JSON, or null when none is stored.</summary>
    Task<string?> GetSummaryProtocolJsonAsync(string eventFolderPath, CancellationToken cancellationToken = default);

    /// <summary>Stores (inserts/updates) the competition-level summary-protocol template JSON.</summary>
    Task SaveSummaryProtocolJsonAsync(string eventFolderPath, string json, CancellationToken cancellationToken = default);

    /// <summary>Returns the competition-level online-publish settings JSON, or null when none is stored (the
    /// caller then seeds defaults from the competition metadata).</summary>
    Task<string?> GetOnlinePublishJsonAsync(string eventFolderPath, CancellationToken cancellationToken = default);

    /// <summary>Stores (inserts/updates) the competition-level online-publish settings JSON.</summary>
    Task SaveOnlinePublishJsonAsync(string eventFolderPath, string json, CancellationToken cancellationToken = default);
}
