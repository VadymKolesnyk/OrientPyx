using OrientPyx.BusinessLogic.Enums;

namespace OrientPyx.BusinessLogic.Models;

/// <summary>
/// Flat read/write model for one participant on one day: joins a <c>Participant</c> (identity fields,
/// shared across days) with its <c>ParticipantDay</c> link (this day's group and chip) so the UI
/// handles a single row per competitor on the day. <see cref="GroupId"/> null means "no group yet";
/// <see cref="GroupName"/> carries the resolved name for display. <see cref="DayDefaultDiscipline"/>
/// lets the row decide whether the discipline-specific Team column is relevant.
/// </summary>
public sealed record ParticipantDayRow(
    Guid LinkId,
    // The day this row belongs to, so the row can ask what this one day costs (per-day payment mode).
    Guid DayId,
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
    // This day's own payment («Оплата» in per-day payment mode); ignored while the competition pays once
    // for the whole competition (then the competition-level Payment above is the live value).
    string DayPayment,
    // Whether the competition charges the entry fee per day, so the row knows which payment field the
    // «Оплата» column edits and which fee the payment is compared against.
    bool PaymentPerDay,
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
    // The judge's points correction («бонус») for this day; null = none. Editable on point-scoring days,
    // already folded into Result.Score by ComputeDayResultsAsync.
    int? Bonus,
    DisciplineType DayDefaultDiscipline,
    // Computed run result for this day (read-only except Status, which a judge may override). See
    // ParticipantDayResult; Empty when the chip was never read.
    ParticipantDayResult Result);

/// <summary>One participating day's fee inputs: which day it is, the group assigned (null = none), the
/// chip held and whether the raised (late) fee is flagged for that day (per-day payment mode only). The
/// day id lets the caller ask what that single day costs (per-day payment mode).</summary>
public readonly record struct ParticipantFeeDay(Guid DayId, Guid? GroupId, string Chip, bool PaysRaisedFee)
{
    /// <summary>The shape used outside per-day payment mode, where the flag lives on the participant.</summary>
    public ParticipantFeeDay(Guid dayId, Guid? groupId, string chip) : this(dayId, groupId, chip, false)
    {
    }
}
