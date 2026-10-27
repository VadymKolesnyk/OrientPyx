using OrientPyx.Plugins.Sdk.Toolkit.Patterns;

namespace OrientPyx.Plugins.Sdk.Toolkit;

/// <summary>The variant a scatter run was attributed to, with how far its order was satisfied.</summary>
public sealed record VariantMatch(
    CourseVariantData Variant, int Depth, int Consumed, string? FirstMissing, IReadOnlyList<string> Missed)
{
    /// <summary>True when the whole variant was satisfied in order.</summary>
    public bool Ok => FirstMissing is null;
}

/// <summary>
/// Picks the scatter («розсіювання») variant a runner ran from their punches. Every variant is matched
/// (<see cref="Match"/>); a full match beats a partial one, then more controls satisfied (<c>Depth</c>), then more
/// punches consumed, then the first-defined variant.
/// </summary>
public static class VariantMatcher
{
    public static VariantMatch Best(IReadOnlyList<CourseVariantData> variants, IReadOnlyList<string> punched)
    {
        if (variants.Count == 0)
            throw new ArgumentException("At least one variant is required.", nameof(variants));

        (CourseVariantData Variant, MatchResult Match)? best = null;
        foreach (var variant in variants)
        {
            var match = Match(variant, punched);
            if (best is not { } b || IsBetter(match, b.Match))
                best = (variant, match);
        }

        var (v, m) = best!.Value;
        return new VariantMatch(v, m.Depth, m.Consumed, m.FirstMissing, m.Missed);
    }

    /// <summary>
    /// Matches one course against the punches. A plain order is walked greedily (<see cref="CourseMatcher.MatchOrder"/>);
    /// a «mixed» pattern goes through <see cref="CourseMatcher.MatchPattern(CoursePattern, IReadOnlyList{string}, IReadOnlySet{string})"/>
    /// (never expanded into its orders — an any-of block grows factorially), its depth being the required count
    /// minus the missed controls and its consumed count the punches the pattern took. The pattern's controls that
    /// are not among <see cref="CourseVariantData.Controls"/> are the day's disabled ones and count as satisfied.
    /// </summary>
    public static MatchResult Match(CourseVariantData variant, IReadOnlyList<string> punched)
    {
        if (!variant.IsPattern)
            return CourseMatcher.MatchOrder(variant.Controls, punched);

        var pattern = variant.Pattern();
        var kept = new HashSet<string>(variant.Controls.Select(c => c.Trim()), StringComparer.OrdinalIgnoreCase);
        var ignored = new HashSet<string>(
            pattern.ControlCodes.Where(c => !kept.Contains(c)), StringComparer.OrdinalIgnoreCase);

        var ok = pattern.Match(punched, ignored, out var onCourse, out var firstMissing);
        var consumed = onCourse.Count(c => c);
        if (ok)
            return new MatchResult(true, null, [], pattern.RequiredCount, consumed);

        var missed = MissedControls.ForPattern(pattern, punched, ignored);
        return new MatchResult(false, firstMissing, missed, Math.Max(0, pattern.RequiredCount - missed.Count), consumed);
    }

    // Better: full beats partial; then more controls satisfied; then more punches consumed. The incumbent is
    // kept on a tie, so the first-defined variant wins any remaining tie.
    private static bool IsBetter(MatchResult candidate, MatchResult incumbent)
    {
        if (candidate.Ok != incumbent.Ok)
            return candidate.Ok;
        if (candidate.Depth != incumbent.Depth)
            return candidate.Depth > incumbent.Depth;
        return candidate.Consumed > incumbent.Consumed;
    }
}
