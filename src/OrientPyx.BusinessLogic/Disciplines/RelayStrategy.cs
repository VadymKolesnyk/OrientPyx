using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Disciplines;

/// <summary>
/// Relay (естафета, Р-20…Р-38): teams of runners on consecutive legs. Each leg is judged like a mixed course
/// against the group's course order (a bare row is a plain set course, so any «змішаний» pattern works); the day
/// calculator walks the legs in order (leg N starts from leg N−1's finish, or the restart) and sums the leg times.
/// This strategy owns the per-leg rules and the team ranking; the leg sequencing lives in the calculator because
/// it needs the whole team at once.
/// </summary>
public sealed class RelayStrategy : DisciplineStrategyBase
{
    private readonly MixedStrategy _leg;

    public RelayStrategy(ICourseDistanceCalculator distance) => _leg = new MixedStrategy(distance);

    public override DisciplineId Id => DisciplineId.Relay;

    public override CompetitorModel Competitors => CompetitorModel.TeamWithLegs;

    /// <summary>The leg course is a pattern — the groups page previews and validates it.</summary>
    public override bool UsesCoursePattern => true;

    public override bool ChecksStartControlClash => true;

    /// <summary>Р-38: no sports ranks for a relay.</summary>
    public override bool AwardsRanks => false;

    public override bool UsesColumn(GroupColumn column) => column switch
    {
        GroupColumn.CourseOrder => true,
        _ => base.UsesColumn(column)
    };

    public override int ControlCount(string courseOrder) => _leg.ControlCount(courseOrder);

    /// <summary>A leg: DNF without a finish, MP against the leg course, else OK. The time limit applies to the
    /// team (Р-31), so the calculator passes no limit for a leg.</summary>
    public override FinishStatusResult EvaluateFinish(FinishContext context) => _leg.EvaluateFinish(context);

    public override SplitsView BuildSplits(SplitsContext context) => _leg.BuildSplits(context);

    /// <summary>
    /// Р-35: only OK, in-competition, ranked teams are placed, by team time; equal times share a place.
    /// <see cref="RelayRestartRanking.AfterClean"/> places restarted teams after every clean one,
    /// <see cref="RelayRestartRanking.Unranked"/> leaves them «П/К».
    /// </summary>
    public override void Rank(GroupRankingContext context)
    {
        var mode = context.Settings?.RestartRanking ?? RelayRestartRanking.AfterClean;
        if (mode == RelayRestartRanking.Unranked)
            foreach (var team in context.RelayTeams.Where(t => t.Restarted))
                context.TeamResults[team.TeamId] = context.TeamResults[team.TeamId] with { OutOfCompetition = true };

        var placeable = context.RelayTeams
            .Select(t => context.TeamResults[t.TeamId])
            .Where(t => t.Status == FinishStatus.Ok && !t.OutOfCompetition && t.UnrankedReason is null && t.Time is not null)
            .ToList();

        var order = Ranking.ByTime<TeamDayResult>(t => t.Time);
        if (mode == RelayRestartRanking.AfterClean)
        {
            var clean = placeable.Where(t => !t.Restarted).ToList();
            Place(context, clean, order, offset: 0);
            Place(context, placeable.Where(t => t.Restarted), order, offset: clean.Count);
        }
        else
            Place(context, placeable, order, offset: 0);
    }

    private static void Place(
        GroupRankingContext context, IEnumerable<TeamDayResult> teams, IComparer<TeamDayResult> order, int offset)
    {
        foreach (var (team, place) in Ranking.AssignPlaces(teams, order))
            context.TeamResults[team.TeamId] = context.TeamResults[team.TeamId] with { Place = place + offset };
    }
}
