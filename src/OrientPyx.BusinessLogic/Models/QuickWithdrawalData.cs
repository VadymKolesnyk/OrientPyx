using OrientPyx.BusinessLogic.Enums;

namespace OrientPyx.BusinessLogic.Models;

/// <summary>
/// Data for the «Швидке зняття» (quick withdrawal) editor on one day: every participant on the day
/// keyed by their start number, so the dialog can resolve a typed number to a competitor (auto-filling
/// the surname) and warn before setting <see cref="FinishStatus.Dns"/> on someone whose chip has already
/// been read. The user builds a short list of «number → status» rows; on save each is written back as a
/// judge's manual status override (see <c>SetParticipantDayResultStatusAsync</c>).
/// </summary>
public sealed record QuickWithdrawalData(IReadOnlyList<QuickWithdrawalMember> Members);

/// <summary>
/// One competitor on the day for the quick-withdrawal editor: their identity (so the override can be
/// written back), the start number the user types to find them, the display surname/name, whether their
/// chip has already been read on the day (which forbids marking DNS), and the current manual override so
/// an already-withdrawn competitor shows their status when the dialog opens.
/// </summary>
/// <param name="ParticipantId">The competition-level participant id.</param>
/// <param name="Number">The participant's start number (may be blank when unassigned).</param>
/// <param name="FullName">The participant's display name (surname first, as stored).</param>
/// <param name="HasReadout">True when a finish read-out on the day matched this participant's chip —
/// blocks setting <see cref="FinishStatus.Dns"/> (they clearly started).</param>
/// <param name="CurrentStatus">The current manual status override, or null when none is set.</param>
public sealed record QuickWithdrawalMember(
    Guid ParticipantId,
    string Number,
    string FullName,
    bool HasReadout,
    FinishStatus? CurrentStatus);
