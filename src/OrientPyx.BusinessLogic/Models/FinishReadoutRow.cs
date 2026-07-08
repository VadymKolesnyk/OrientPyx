using OrientPyx.BusinessLogic.Enums;

namespace OrientPyx.BusinessLogic.Models;

/// <summary>
/// A read-only row in the finish-read log for the current day: the persisted read-out joined with the
/// participant who holds that chip on this day (when any). The participant fields are blank and
/// <see cref="IsKnown"/> is false when no participant on the day carries the chip (an unrecognised read).
/// <see cref="Status"/> is the derived finish status (only for a known participant on a discipline that
/// evaluates it; <see cref="FinishStatus.None"/> otherwise), with <see cref="StatusDetail"/> a short
/// explanation (e.g. the first missing control) for the tooltip. <see cref="ResolvedStartTime"/> is the
/// effective start used for evaluation (the chip's own read-out start, else the participant's assigned
/// start paired with the finish's date); <see cref="Elapsed"/> is the resulting finish − start duration,
/// both null when no participant/finish/start is known. <see cref="Score"/> is the collected «Бали»
/// (rogaine and other point-scoring formats); null when the discipline does not score points.
/// <see cref="Place"/> is the participant's 1-based rank within their group on the day (rogaine ranks by
/// score then time, others by time), only assignable for an OK result; null when it cannot be assigned
/// (unknown chip, non-OK status, out-of-competition runner). <see cref="Gap"/> is the loss to the group
/// leader (this OK, placed runner's result time minus the group's place-1 time); null for the leader
/// themselves and for anyone without a place.
/// <see cref="CollectRentalChip"/> is true when the read chip is a rental chip that should be collected
/// from the runner now — i.e. this is the last day the holder uses that rental chip (they hold it on no
/// later day). It drives the chip cell's "collect the rental chip" highlight on the finish read-out.
/// <see cref="IsManualStatus"/> is true when <see cref="Status"/> is a judge's manual override (not the
/// discipline's computed status); it drives the status cell's emphasis — bold, and bold-green when the
/// override is <see cref="FinishStatus.Ok"/>.
/// </summary>
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
    bool IsManualStatus = false);
