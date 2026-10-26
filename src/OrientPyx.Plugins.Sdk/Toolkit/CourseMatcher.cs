using OrientPyx.Plugins.Sdk.Toolkit.Patterns;

namespace OrientPyx.Plugins.Sdk.Toolkit;

/// <summary>
/// The outcome of matching a run's punches against a course.
/// </summary>
/// <param name="Ok">True when every required control was taken in a valid order.</param>
/// <param name="FirstMissing">The first required control the (greedy) walk could not place — the text of an MP
/// detail; null when <paramref name="Ok"/>.</param>
/// <param name="Missed">Every required control left out of the <b>maximum</b> in-order match (Р-52a); empty when OK.</param>
/// <param name="Depth">Required controls satisfied by the greedy walk (ordered courses only).</param>
/// <param name="Consumed">Punches the greedy walk advanced through (ordered courses only).</param>
public sealed record MatchResult(bool Ok, string? FirstMissing, IReadOnlyList<string> Missed, int Depth = 0, int Consumed = 0);

/// <summary>
/// Checks punches against a prescribed course — a plain order (set course, a scatter variant) or a «mixed»
/// pattern. The OK/MP verdict and <see cref="MatchResult.FirstMissing"/> come from the greedy walk the built-in
/// disciplines have always used; <see cref="MatchResult.Missed"/> is the full list from the maximum match.
/// Codes compare trimmed and case-insensitively; extra/foreign punches are ignored.
/// </summary>
public static class CourseMatcher
{
    /// <summary>Matches a prescribed order: every expected control must appear, in order, within the punches.</summary>
    public static MatchResult MatchOrder(IReadOnlyList<string> expected, IReadOnlyList<string> punched)
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
                return new MatchResult(false, control.Trim(), MissedControls.ForOrder(expected, punched), depth, p);
            depth++;
        }
        return new MatchResult(true, null, [], depth, p);
    }

    /// <summary>
    /// Matches a «mixed» pattern (<c>&lt;…&gt;</c> ordered runs, <c>[N: …]</c> any-of blocks, nesting).
    /// <paramref name="ignored"/> (disabled controls) count as satisfied wherever the pattern requires them.
    /// </summary>
    public static MatchResult MatchPattern(
        CoursePattern pattern, IReadOnlyList<string> punched, IReadOnlySet<string>? ignored = null)
    {
        ignored ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ok = pattern.Match(punched, ignored, out _, out var firstMissing);
        return ok
            ? new MatchResult(true, null, [])
            : new MatchResult(false, firstMissing, MissedControls.ForPattern(pattern, punched, ignored));
    }

    /// <summary>Parses <paramref name="patternText"/> and matches it.</summary>
    public static MatchResult MatchPattern(
        string patternText, IReadOnlyList<string> punched, IReadOnlySet<string>? ignored = null) =>
        MatchPattern(CoursePattern.Parse(patternText), punched, ignored);

    /// <summary>
    /// Assigns each punch the prescribed position it takes (or -1 for an extra), maximising the prescribed
    /// controls taken in order — a longest-common-subsequence alignment used for the splits passage. A greedy
    /// walk would let an early stray punch of a later control (1 5 2 3 4 5 6) jump ahead; the alignment ignores
    /// that stray. Among equally good alignments it takes the earliest punch of each control.
    /// </summary>
    public static int[] AlignOrder(IReadOnlyList<string> punched, IReadOnlyList<string> expected)
    {
        var n = punched.Count;
        var m = expected.Count;
        var codes = punched.Select(p => p.Trim()).ToArray();
        var course = expected.Select(c => c.Trim()).ToArray();

        bool Same(int i, int j) =>
            codes[i].Length > 0 && string.Equals(codes[i], course[j], StringComparison.OrdinalIgnoreCase);

        // best[i, j] = most prescribed controls takeable from punch i onwards against course position j onwards.
        var best = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
            for (var j = m - 1; j >= 0; j--)
                best[i, j] = Same(i, j)
                    ? Math.Max(best[i + 1, j + 1] + 1, Math.Max(best[i + 1, j], best[i, j + 1]))
                    : Math.Max(best[i + 1, j], best[i, j + 1]);

        var assignment = new int[n];
        Array.Fill(assignment, -1);
        for (int i = 0, j = 0; i < n && j < m;)
        {
            if (Same(i, j) && best[i, j] == best[i + 1, j + 1] + 1)
            {
                assignment[i] = j;
                i++;
                j++;
            }
            else if (best[i + 1, j] == best[i, j])
                i++;   // this punch is an extra
            else
                j++;   // this prescribed control is missed
        }
        return assignment;
    }
}
