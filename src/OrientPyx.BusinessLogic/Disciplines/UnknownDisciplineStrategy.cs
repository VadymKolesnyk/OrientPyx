using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Disciplines;

/// <summary>
/// Placeholder for a discipline id with no registered strategy — typically a plugin that is missing or
/// disabled (Р-2). Punches are judged like a set course so status and time still show, but nobody is placed
/// (so no points) and no ranks are awarded. <see cref="IsUnknown"/> lets the pages show «Потрібен плагін».
/// </summary>
public sealed class UnknownDisciplineStrategy : DisciplineStrategyBase
{
    private readonly SetCourseStrategy _fallback;

    public UnknownDisciplineStrategy(DisciplineId id, ICourseDistanceCalculator distance)
    {
        Id = id;
        _fallback = new SetCourseStrategy(distance);
    }

    public override DisciplineId Id { get; }

    public override string NameKey => "Discipline.Type.Unknown";

    public override bool IsUnknown => true;

    public override bool AwardsRanks => false;

    public override bool UsesColumn(GroupColumn column) => _fallback.UsesColumn(column);

    public override FinishStatusResult EvaluateFinish(FinishContext context) => _fallback.EvaluateFinish(context);

    public override SplitsView BuildSplits(SplitsContext context) => _fallback.BuildSplits(context);

    /// <summary>No places for an unknown discipline (Р-2).</summary>
    public override void Rank(RankingContext context)
    {
    }
}
