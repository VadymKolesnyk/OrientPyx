using OrientDesk.BusinessLogic.Entities;

namespace OrientDesk.BusinessLogic.Models;

/// <summary>
/// The inputs a <see cref="PointsRule"/> needs to award «Очки» (ranking points) to one runner: the
/// runner's place and (rogaine) score, their result time, and the group context (leader time/score,
/// group size) the formula variables reference. Times are in seconds; null ⇒ unknown (treated as 0
/// by the formula, and as "no time" by a table rule, which only needs the place).
/// </summary>
public sealed record PointsRuleInput(
    int? Place,
    double? ResultTimeSeconds,
    int? Score,
    double? LeaderTimeSeconds,
    int? LeaderScore,
    int GroupSize);

/// <summary>
/// Awards «Очки» (ranking points) for one runner from a <see cref="PointsRule"/>. A
/// <see cref="PointsRuleKind.Table"/> rule looks the runner's <see cref="PointsRuleInput.Place"/> up in
/// the placement list (1st place = first value; a place beyond the list scores 0). A
/// <see cref="PointsRuleKind.Formula"/> rule evaluates the stored expression against a
/// <see cref="PointsFormulaContext"/> built from the input via <see cref="PointsFormula"/>.
///
/// Both yield a decimal rounded to two fractional digits (the canonical points precision). A null result
/// means the rule could not award points (no rule, an unplaced runner for a table rule, or an invalid
/// formula) — the caller leaves «Очки» blank.
/// </summary>
public static class PointsRuleEvaluator
{
    /// <summary>
    /// Computes the points for <paramref name="input"/> under <paramref name="rule"/>, rounded to 2 dp.
    /// Returns null when no points can be awarded (see the class summary).
    /// </summary>
    public static decimal? Evaluate(PointsRule? rule, PointsRuleInput input)
    {
        if (rule is null)
            return null;

        var points = rule.Kind switch
        {
            PointsRuleKind.Table => EvaluateTable(rule.TableJson, input.Place),
            PointsRuleKind.Formula => EvaluateFormula(rule.Formula, input),
            _ => null
        };

        // Points can never be negative: a formula that works out below zero (e.g. a runner more than twice
        // the leader's time in 100*(2 − T_у/T_л)), or a table with a negative entry, is floored to 0.
        return points is { } p && p < 0m ? 0m : points;
    }

    // A placement table awards points only to a placed runner; a place beyond the list scores 0.
    private static decimal? EvaluateTable(string? tableJson, int? place)
    {
        if (place is not { } p || p < 1)
            return null;

        var values = PointsTable.Parse(tableJson);
        var points = p <= values.Count ? values[p - 1] : 0m;
        return PointsTable.Normalize(points);
    }

    // A formula awards points to any runner with the variables resolved (an unplaced runner still has a
    // time/score). A blank or malformed formula awards nothing rather than throwing.
    private static decimal? EvaluateFormula(string? formula, PointsRuleInput input)
    {
        if (!PointsFormula.TryValidate(formula, out _))
            return null;

        var context = new PointsFormulaContext
        {
            ParticipantTime = input.ResultTimeSeconds ?? 0,
            LeaderTime = input.LeaderTimeSeconds ?? 0,
            GroupSize = input.GroupSize,
            Place = input.Place ?? 0,
            Score = input.Score ?? 0,
            LeaderScore = input.LeaderScore ?? 0
        };
        var value = PointsFormula.Evaluate(formula!, context);
        if (double.IsNaN(value) || double.IsInfinity(value))
            return null;
        return PointsTable.Normalize((decimal)value);
    }
}
