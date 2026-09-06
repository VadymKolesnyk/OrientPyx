using OrientPyx.BusinessLogic.Enums;

namespace OrientPyx.BusinessLogic.Models;

/// <summary>
/// A read-only row in the finish-read log for the current day: the persisted read-out joined with the
/// participant who holds that chip on this day (when any).
/// </summary>
/// <param name="IsKnown">False when no participant on the day carries the chip (an unrecognised read);
/// the participant fields are then blank.</param>
/// <param name="Status">The derived finish status — only for a known participant on a discipline that
/// evaluates it, <see cref="FinishStatus.None"/> otherwise.</param>
/// <param name="StatusDetail">Short tooltip explanation (e.g. the first missing control), worded per
/// <paramref name="StatusDetailKind"/>.</param>
/// <param name="ResolvedStartTime">The effective start used for evaluation: the chip's own read-out
/// start, else the participant's assigned start paired with the finish's date.</param>
/// <param name="Elapsed">Finish − <paramref name="ResolvedStartTime"/>; null when no
/// participant/finish/start is known.</param>
/// <param name="Score">Collected «Бали» for point-scoring formats; null when the discipline scores none.</param>
/// <param name="Place">1-based rank within the group on the day (rogaine by score then time, others by
/// time). Only assignable for an OK result; null for an unknown chip, a non-OK status or an
/// out-of-competition runner.</param>
/// <param name="Gap">Loss to the group leader (this runner's result time minus the group's place-1 time);
/// null for the leader themselves and for anyone without a place.</param>
/// <param name="CollectRentalChip">True when the chip is a rental one to collect now — the holder uses it
/// on no later day. Drives the chip cell's "collect the rental chip" highlight.</param>
/// <param name="IsManualStatus">True when <paramref name="Status"/> is a judge's override, not the
/// computed status. Drives the status cell's emphasis: bold, and bold-green when the override is
/// <see cref="FinishStatus.Ok"/>.</param>
public sealed record FinishReadoutRow(
    Guid Id,
    int Order,
    string ChipNumber,
    DateTimeOffset? StartTime,
    DateTimeOffset? FinishTime,
    bool IsKnown,
    string ParticipantNumber,
    string FullName,
    string GroupName,
    FinishStatus Status,
    string StatusDetail,
    DateTimeOffset? ResolvedStartTime,
    TimeSpan? Elapsed,
    int? Score = null,
    int? Place = null,
    TimeSpan? Gap = null,
    bool CollectRentalChip = false,
    bool IsManualStatus = false,
    FinishDetailKind StatusDetailKind = FinishDetailKind.MissingControl);
