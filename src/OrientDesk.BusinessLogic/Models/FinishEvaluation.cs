using OrientDesk.BusinessLogic.Enums;

namespace OrientDesk.BusinessLogic.Models;

/// <summary>
/// Everything a discipline needs to derive a participant's finish status from one read-out, all as
/// layer-neutral data. Times are optional (a read-out / assignment may lack them). The expected
/// controls are the group's prescribed course already reduced to the codes that must be visited
/// (start/finish markers removed by the caller using the day's control-point types).
/// </summary>
public sealed class FinishContext
{
    /// <summary>Control codes that must be visited, in the prescribed order (no start/finish markers).</summary>
    public IReadOnlyList<string> ExpectedControls { get; init; } = [];

    /// <summary>Control codes the chip actually punched, in read order (start/finish already excluded).</summary>
    public IReadOnlyList<string> PunchedControls { get; init; } = [];

    /// <summary>Start time used for the time-limit check (caller picks chip-then-assigned). Null = unknown.</summary>
    public DateTimeOffset? StartTime { get; init; }

    /// <summary>Finish time from the read-out. Null = no finish punch.</summary>
    public DateTimeOffset? FinishTime { get; init; }

    /// <summary>The group's time limit (контрольний час) for the day, when set; null = no limit.</summary>
    public TimeSpan? TimeLimit { get; init; }
}

/// <summary>
/// A computed finish status plus a short human-readable detail (e.g. which control is missing), shown
/// as a tooltip next to the status. <see cref="Detail"/> is empty when there is nothing to add.
/// </summary>
public readonly record struct FinishStatusResult(FinishStatus Status, string Detail)
{
    public static FinishStatusResult Of(FinishStatus status) => new(status, string.Empty);
}
