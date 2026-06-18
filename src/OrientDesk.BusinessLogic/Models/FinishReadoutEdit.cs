using OrientDesk.BusinessLogic.Enums;

namespace OrientDesk.BusinessLogic.Models;

/// <summary>
/// One pickable participant for the finish-read edit modal's "reassign chip" dropdown: the day member's
/// id plus a display label (bib + ПІБ + group). Independent of any UI type.
/// </summary>
/// <param name="ParticipantId">The participant's id (day member).</param>
/// <param name="Number">The participant's bib number (may be blank).</param>
/// <param name="FullName">The participant's full name.</param>
/// <param name="GroupName">The participant's group on the day (may be blank).</param>
public sealed record FinishReadoutParticipantOption(
    Guid ParticipantId,
    string Number,
    string FullName,
    string GroupName);

/// <summary>
/// Everything the finish-read edit modal needs to open for one logged read-out: its current editable
/// values (chip, times, punches, status) and the day's participants the chip can be reassigned to, plus
/// the participant who currently holds the chip on the day (when any) so the dropdown opens on them.
/// </summary>
public sealed class FinishReadoutEditData
{
    /// <summary>The read-out being edited (its stable id).</summary>
    public Guid Id { get; init; }

    /// <summary>The chip number as currently stored.</summary>
    public string ChipNumber { get; init; } = string.Empty;

    /// <summary>Start time, or null when none.</summary>
    public DateTimeOffset? StartTime { get; init; }

    /// <summary>Finish time, or null when none.</summary>
    public DateTimeOffset? FinishTime { get; init; }

    /// <summary>The control punches in order (code + time), as currently stored.</summary>
    public IReadOnlyList<ChipPunch> Punches { get; init; } = [];

    /// <summary>The effective status currently shown (the manual override when set, else the computed one).</summary>
    public FinishStatus Status { get; init; }

    /// <summary>True when the shown status is a manual override (vs the discipline's computed status).</summary>
    public bool HasManualStatus { get; init; }

    /// <summary>The day's participants the chip can be reassigned to, ordered for display.</summary>
    public IReadOnlyList<FinishReadoutParticipantOption> Participants { get; init; } = [];

    /// <summary>The participant who currently holds this chip on the day, or null when unrecognised.</summary>
    public Guid? CurrentHolderId { get; init; }
}

/// <summary>
/// The confirmed result of the finish-read edit modal: the edited read-out fields plus, optionally, the
/// participant the chip should be (re)assigned to on the day. <see cref="ManualStatus"/> is the chosen
/// status override (null = leave to automatic evaluation). <see cref="ReassignToParticipantId"/> is null
/// when the chip's holder is left unchanged.
/// </summary>
public sealed class FinishReadoutEdit
{
    public Guid Id { get; init; }
    public string ChipNumber { get; init; } = string.Empty;
    public DateTimeOffset? StartTime { get; init; }
    public DateTimeOffset? FinishTime { get; init; }
    public IReadOnlyList<ChipPunch> Punches { get; init; } = [];
    public FinishStatus? ManualStatus { get; init; }
    public Guid? ReassignToParticipantId { get; init; }
}
