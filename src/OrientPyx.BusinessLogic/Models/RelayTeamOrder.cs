namespace OrientPyx.BusinessLogic.Models;

/// <summary>
/// The order a relay group's teams are listed in on every results output — the protocol, the monitor, the HTML
/// splits and the online snapshot (the spectator site sorts the same way from the published statuses and legs):
/// <list type="number">
/// <item>placed teams, by place;</item>
/// <item>finished teams with no place («П/К», withheld from ranking), by team time;</item>
/// <item>teams still running with nobody withdrawn and legs 1…k finished one after another — more legs first,
/// then the cumulative time after leg k;</item>
/// <item>teams still running with no leg finished yet;</item>
/// <item>withdrawn teams (MP / OVT / DNF / DSQ), then DNS, by team time.</item>
/// </list>
/// Equal keys fall back to the team number (a team without a number after the numbered ones).
/// </summary>
public static class RelayTeamOrder
{
    /// <summary>The teams in standing order (<see cref="Key"/>, then the team number).</summary>
    public static IOrderedEnumerable<T> Sort<T>(IEnumerable<T> teams, Func<T, TeamDayResult?> result, Func<T, int?> number) =>
        teams.OrderBy(t => Key(result(t))).ThenBy(t => RelayTeamNumbers.SortKey(number(t)));

    /// <summary>The ascending sort key of a team result: stage, then the place / legs finished (negated), then a time.</summary>
    public static (int Stage, int Rank, TimeSpan Time) Key(TeamDayResult? result)
    {
        if (result is null)
            return (3, 0, TimeSpan.MaxValue);
        if (result.Place is { } place && !result.OutOfCompetition)
            return (0, place, TimeSpan.Zero);
        switch (result.Status)
        {
            case FinishStatus.Ok:
                return (1, 0, result.Time ?? TimeSpan.MaxValue);
            case FinishStatus.None:
                var (legs, cumulative) = FinishedLegs(result);
                return legs > 0 ? (2, -legs, cumulative) : (3, 0, TimeSpan.MaxValue);
            case FinishStatus.Dns:
                return (5, 0, result.Time ?? TimeSpan.MaxValue);
            default:
                return (4, 0, result.Time ?? TimeSpan.MaxValue);
        }
    }

    /// <summary>How many legs from leg 1 on are finished OK with no gap, and the team's cumulative time after the
    /// last of them (<see cref="TimeSpan.Zero"/> when none is).</summary>
    public static (int Legs, TimeSpan Cumulative) FinishedLegs(TeamDayResult result)
    {
        var legs = 0;
        var cumulative = TimeSpan.Zero;
        foreach (var leg in result.Legs.OrderBy(l => l.Leg))
        {
            if (leg.Status != FinishStatus.Ok || leg.Cumulative is not { } c)
                break;
            legs++;
            cumulative = c;
        }
        return (legs, cumulative);
    }
}
