using OrientDesk.BusinessLogic.Enums;

namespace OrientDesk.BusinessLogic.Models;

/// <summary>
/// A read-only row in the finish-read log for the current day: the persisted read-out joined with the
/// participant who holds that chip on this day (when any). The participant fields are blank and
/// <see cref="IsKnown"/> is false when no participant on the day carries the chip (an unrecognised read).
/// <see cref="Status"/> is the derived finish status (only for a known participant on a discipline that
/// evaluates it; <see cref="FinishStatus.None"/> otherwise), with <see cref="StatusDetail"/> a short
/// explanation (e.g. the first missing control) for the tooltip. <see cref="ResolvedStartTime"/> is the
/// effective start used for evaluation (the chip's own read-out start, else the participant's assigned
/// start paired with the finish's date); <see cref="Elapsed"/> is the resulting finish − start duration,
/// both null when no participant/finish/start is known.
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
    TimeSpan? Elapsed);
