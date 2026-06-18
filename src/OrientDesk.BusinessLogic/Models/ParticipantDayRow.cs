using OrientDesk.BusinessLogic.Enums;

namespace OrientDesk.BusinessLogic.Models;

/// <summary>
/// Flat read/write model for one participant on one day: joins a <c>Participant</c> (identity fields,
/// shared across days) with its <c>ParticipantDay</c> link (this day's group and chip) so the UI
/// handles a single row per competitor on the day. <see cref="GroupId"/> null means "no group yet";
/// <see cref="GroupName"/> carries the resolved name for display. <see cref="DayDefaultDiscipline"/>
/// lets the row decide whether the discipline-specific Team column is relevant.
/// </summary>
public sealed record ParticipantDayRow(
    Guid LinkId,
    Guid ParticipantId,
    int Order,
    string FullName,
    string Number,
    string Rank,
    string Coach,
    DateTimeOffset? BirthDate,
    Guid? RegionId,
    string RegionName,
    Guid? ClubId,
    string ClubName,
    Guid? DusshId,
    string DusshName,
    string Representative,
    string FsouCode,
    bool IsFsouMember,
    string Payment,
    string Note,
    bool PaysRaisedFee,
    IReadOnlyList<Guid> SelectedDiscountIds,
    decimal TotalEntryFee,
    // The participant's (group, chip) on every OTHER day they run (not this one). The fee total spans
    // all days, so a live recompute in the day grid combines these fixed contributions with this day's
    // live group/chip — see ParticipantDayRowViewModel.RecomputeTotal.
    IReadOnlyList<ParticipantFeeDay> OtherDays,
    Guid? GroupId,
    string GroupName,
    string Chip,
    string Team,
    TimeSpan? StartTime,
    bool OutOfCompetition,
    DisciplineType DayDefaultDiscipline,
    // Computed run result for this day (read-only except Status, which a judge may override). See
    // ParticipantDayResult; Empty when the chip was never read.
    ParticipantDayResult Result);

/// <summary>One participating day's fee inputs: the group assigned (null = none) and the chip held.</summary>
public readonly record struct ParticipantFeeDay(Guid? GroupId, string Chip);
