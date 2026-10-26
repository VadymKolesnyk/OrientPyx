namespace OrientPyx.Plugins.Sdk.Toolkit;

/// <summary>The variant a scatter run was attributed to, with how far its order was satisfied.</summary>
public sealed record VariantMatch(
    ScatterVariantData Variant, int Depth, int Consumed, string? FirstMissing, IReadOnlyList<string> Missed)
{
    /// <summary>True when the whole variant was satisfied in order.</summary>
    public bool Ok => FirstMissing is null;
}

/// <summary>
/// Picks the scatter («розсіювання») variant a runner ran from their punches. Every variant is walked greedily
/// (<see cref="CourseMatcher.MatchOrder"/>); a full match beats a partial one, then more controls satisfied
/// (<c>Depth</c>), then more punches consumed, then the first-defined variant.
/// </summary>
public static class VariantMatcher
{
    public static VariantMatch Best(IReadOnlyList<ScatterVariantData> variants, IReadOnlyList<string> punched)
    {
        if (variants.Count == 0)
            throw new ArgumentException("At least one variant is required.", nameof(variants));

        (ScatterVariantData Variant, MatchResult Match)? best = null;
        foreach (var variant in variants)
        {
            var match = CourseMatcher.MatchOrder(variant.Controls, punched);
            if (best is not { } b || IsBetter(match, b.Match))
                best = (variant, match);
        }

        var (v, m) = best!.Value;
        return new VariantMatch(v, m.Depth, m.Consumed, m.FirstMissing, m.Missed);
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
