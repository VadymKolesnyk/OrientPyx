using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Services.Iof;

/// <summary>The program's result status as an IOF <c>ResultStatus</c> (plan §3.1).</summary>
internal static class IofStatusMap
{
    /// <summary>
    /// OK is <c>OK</c>, or <c>NotCompeting</c> for a runner «поза конкурсом» (a non-OK «П/К» keeps its own status — MP
    /// says more than NotCompeting). No status: <c>Finished</c> with a read-out (finished, not judged); without one
    /// <c>DidNotStart</c> once the day is closed, else <c>Inactive</c> («not started yet» during the day).
    /// </summary>
    public static IofResultStatus Of(FinishStatus status, bool outOfCompetition, bool hasReadout, bool dayLocked) =>
        status switch
        {
            FinishStatus.Ok => outOfCompetition ? IofResultStatus.NotCompeting : IofResultStatus.OK,
            FinishStatus.Mp => IofResultStatus.MissingPunch,
            FinishStatus.Ovt => IofResultStatus.OverTime,
            FinishStatus.Dnf => IofResultStatus.DidNotFinish,
            FinishStatus.Dns => IofResultStatus.DidNotStart,
            FinishStatus.Dsq => IofResultStatus.Disqualified,
            _ => hasReadout ? IofResultStatus.Finished
                : dayLocked ? IofResultStatus.DidNotStart
                : IofResultStatus.Inactive
        };

    /// <summary>A relay team still without a status: running (<c>Active</c>) or not started (<c>Inactive</c>) while the
    /// day is open; once it is closed, <c>DidNotFinish</c> when a leg was run, else <c>DidNotStart</c>.</summary>
    public static IofResultStatus OpenTeam(bool anyLegRead, bool dayLocked) =>
        dayLocked
            ? anyLegRead ? IofResultStatus.DidNotFinish : IofResultStatus.DidNotStart
            : anyLegRead ? IofResultStatus.Active : IofResultStatus.Inactive;

    /// <summary>How bad a status is, for a rogaine team without a place: DSQ &gt; MP &gt; OVT &gt; DNF &gt; DNS.</summary>
    public static int Severity(IofResultStatus status) => status switch
    {
        IofResultStatus.Disqualified => 8,
        IofResultStatus.MissingPunch => 7,
        IofResultStatus.OverTime => 6,
        IofResultStatus.DidNotFinish => 5,
        IofResultStatus.DidNotStart => 4,
        IofResultStatus.Active => 3,
        IofResultStatus.Inactive => 2,
        IofResultStatus.Finished => 1,
        _ => 0
    };
}
