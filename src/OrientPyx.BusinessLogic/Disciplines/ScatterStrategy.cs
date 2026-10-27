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
/// The variants come from the day's <see cref="GroupCourseVariant"/> rows (per group), reduced to their required
/// controls and delivered on the context's <c>CourseVariants</c>. With no variants (a mis-configured group)
/// it degrades gracefully to a plain set-course judgement against the context's <c>ExpectedControls</c>.
/// </summary>
public sealed class ScatterStrategy : DisciplineStrategyBase
{
    private readonly ICourseDistanceCalculator _distance;
    private readonly MixedStrategy _pattern;

    public ScatterStrategy(ICourseDistanceCalculator distance)
    {
        _distance = distance;
        _pattern = new MixedStrategy(distance);
    }

    public override DisciplineId Id => DisciplineId.Scatter;

    /// <summary>The course is the group's set of variants (one <see cref="GroupCourseVariant"/> row each).</summary>
    public override bool UsesCourseVariants => true;

    /// <summary>Each variant is a prescribed order — the draw warns on a shared opening control across variants.</summary>
    public override bool ChecksStartControlClash => true;

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
        var variants = context.CourseVariants;
        var match = variants.Count > 0
            ? VariantMatcher.Best(variants, context.PunchedControls) is var best
                ? new MatchResult(best.Ok, best.FirstMissing, best.Missed)
                : null!
            : CourseMatcher.MatchOrder(context.ExpectedControls, context.PunchedControls);
        if (!match.Ok)
            return new FinishStatusResult(FinishStatus.Mp, match.FirstMissing!) { Missed = match.Missed };

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
        var variants = context.CourseVariants;
        if (variants.Count == 0)
            return BuildOrderedSplits(context, context.ExpectedControls, _distance);

        var punched = context.Punches.Select(p => p.ControlCode.Trim()).Where(c => c.Length > 0).ToList();
        var best = VariantMatcher.Best(variants, punched);
        if (best.Variant.IsPattern)
        {
            // A pattern variant (Р-40) has no single order: render it the «mixed» way against its own pattern.
            var view = _pattern.BuildSplits(context with
            {
                CourseOrderText = best.Variant.CourseOrderText,
                ExpectedControls = best.Variant.Controls
            });
            return view with { VariantCode = best.Variant.Code };
        }
        return BuildOrderedSplits(context, best.Variant.Controls, _distance, best.Variant.Code);
    }
}
