using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Disciplines;

/// <summary>
/// Scatter / butterfly (розсіювання): the course is defined by <b>several</b> valid orders (variants); every
/// runner runs one of them and which one is <b>auto-detected</b> from their read-out. The runner is judged
/// against the best-matching variant — the one whose full order their punches satisfy in sequence (ties broken
/// by the most punches consumed, then the first-defined) — exactly like a set course. When no variant fully
/// matches, the runner is attributed to the one they got <b>furthest through</b> (most controls satisfied in
/// order) and marked <see cref="FinishStatus.Mp"/> with the first control that variant was missing. The splits
/// render the ordered passage against the detected variant, tagged with its <see cref="SplitsView.VariantCode"/>.
///
/// The variants come from the day's <see cref="ScatterVariant"/> rows (per group), reduced to their required
/// controls and delivered on the context's <c>ScatterVariants</c>. With no variants (a mis-configured group)
/// it degrades gracefully to a plain set-course judgement against the context's <c>ExpectedControls</c>.
/// </summary>
public sealed class ScatterStrategy : DisciplineStrategyBase
{
    private readonly ICourseDistanceCalculator _distance;

    public ScatterStrategy(ICourseDistanceCalculator distance) => _distance = distance;

    public override DisciplineType Type => DisciplineType.Scatter;

    public override bool UsesColumn(GroupColumn column) => column switch
    {
        GroupColumn.CourseOrder => true,           // the representative (first) variant order
        _ => base.UsesColumn(column)
    };

    /// <summary>
    /// Same priority as a set course — DNF &gt; MP &gt; OVT &gt; OK — but judged against the best-matching
    /// variant: no finish punch ⇒ <see cref="FinishStatus.Dnf"/>; the selected variant is not fully satisfied
    /// (as a subsequence of the punches) ⇒ <see cref="FinishStatus.Mp"/> with its first missing control; a time
    /// limit is set and exceeded ⇒ <see cref="FinishStatus.Ovt"/>; otherwise <see cref="FinishStatus.Ok"/>.
    /// </summary>
    public override FinishStatusResult EvaluateFinish(FinishContext context)
    {
        if (context.FinishTime is null)
            return FinishStatusResult.Of(FinishStatus.Dnf);

        // No configured variants: fall back to a plain set-course check against the expected controls so a
        // mis-configured scatter group still yields a sensible status rather than always OK.
        var variants = context.ScatterVariants;
        var expected = variants.Count > 0
            ? SelectBestVariant(variants, context.PunchedControls).Variant.Controls
            : context.ExpectedControls;

        var firstMissing = FirstMissing(expected, context.PunchedControls);
        if (firstMissing is not null)
            return new FinishStatusResult(FinishStatus.Mp, firstMissing);

        if (context.TimeLimit is { } limit && context.StartTime is { } start
            && context.FinishTime.Value - start > limit)
            return FinishStatusResult.Of(FinishStatus.Ovt);

        return FinishStatusResult.Of(FinishStatus.Ok);
    }

    /// <summary>
    /// Ordered splits against the auto-detected variant, reusing the shared set-course passage/leg machinery
    /// (<see cref="DisciplineStrategyBase.BuildOrderedSplits"/>). The chosen variant's controls become the
    /// prescribed order; the resulting view is stamped with the variant's code so the panel/printout can show
    /// «Розсіювання: A». With no variants it degrades to the context's expected controls.
    /// </summary>
    public override SplitsView BuildSplits(SplitsContext context)
    {
        var variants = context.ScatterVariants;
        if (variants.Count == 0)
            return BuildOrderedSplits(context, context.ExpectedControls, _distance);

        var punched = context.Punches.Select(p => p.ControlCode.Trim()).Where(c => c.Length > 0).ToList();
        var best = SelectBestVariant(variants, punched);
        return BuildOrderedSplits(context, best.Variant.Controls, _distance, best.Variant.Code);
    }

    /// <summary>
    /// Picks the variant a runner ran, from their punched control codes. Every variant is scored by how far
    /// its prescribed order is satisfied as an in-order subsequence of the punches: <c>Depth</c> = required
    /// controls matched, <c>Consumed</c> = how many punches the greedy walk advanced through, and
    /// <c>FirstMissing</c> = the first required control it could not place (null = the whole variant matched).
    /// Selection: a fully-matched variant wins over a partial one; among equally-good matches prefer the one
    /// that consumed the most punches, then the first-defined (lowest <c>Order</c> in the list). This yields
    /// the "best match, most controls" rule for full matches and the "closest variant" for the MP fallback.
    /// </summary>
    private static (ScatterVariantData Variant, int Depth, int Consumed, string? FirstMissing) SelectBestVariant(
        IReadOnlyList<ScatterVariantData> variants, IReadOnlyList<string> punched)
    {
        (ScatterVariantData Variant, int Depth, int Consumed, string? FirstMissing)? best = null;

        foreach (var variant in variants)
        {
            var (depth, consumed, firstMissing) = ScoreVariant(variant.Controls, punched);
            var candidate = (variant, depth, consumed, firstMissing);

            if (best is not { } b || IsBetter(candidate, b))
                best = candidate;
        }

        // variants is never empty at the call sites, but guard anyway.
        return best ?? (variants[0], 0, 0, variants[0].Controls.Count > 0 ? variants[0].Controls[0] : null);
    }

    // A candidate is better when: it is a full match and the incumbent is not; or both have the same match
    // completeness and it satisfied more controls; or equal depth but consumed more punches. Order in the list
    // (first-defined) breaks any remaining tie by virtue of the incumbent being kept on a non-strict win.
    private static bool IsBetter(
        (ScatterVariantData Variant, int Depth, int Consumed, string? FirstMissing) candidate,
        (ScatterVariantData Variant, int Depth, int Consumed, string? FirstMissing) incumbent)
    {
        var candidateFull = candidate.FirstMissing is null;
        var incumbentFull = incumbent.FirstMissing is null;
        if (candidateFull != incumbentFull)
            return candidateFull;

        if (candidate.Depth != incumbent.Depth)
            return candidate.Depth > incumbent.Depth;

        return candidate.Consumed > incumbent.Consumed;
    }

    // Greedy in-order subsequence walk of one variant's controls over the punches: returns how many prescribed
    // controls were satisfied (Depth), how far into the punch list the walk reached (Consumed), and the first
    // control it could not satisfy (FirstMissing, null when all were). Mirrors SetCourseStrategy.EvaluateFinish.
    private static (int Depth, int Consumed, string? FirstMissing) ScoreVariant(
        IReadOnlyList<string> expected, IReadOnlyList<string> punched)
    {
        var p = 0;
        var depth = 0;
        foreach (var control in expected)
        {
            var found = false;
            while (p < punched.Count)
            {
                var isMatch = string.Equals(punched[p].Trim(), control.Trim(), StringComparison.OrdinalIgnoreCase);
                p++;
                if (isMatch)
                {
                    found = true;
                    break;
                }
            }
            if (!found)
                return (depth, p, control.Trim());
            depth++;
        }
        return (depth, p, null);
    }

    // The first expected control not present, in order, within the punches (null when the whole order matches).
    private static string? FirstMissing(IReadOnlyList<string> expected, IReadOnlyList<string> punched) =>
        ScoreVariant(expected, punched).FirstMissing;
}
