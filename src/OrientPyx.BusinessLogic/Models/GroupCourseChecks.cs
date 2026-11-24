using System.Text.RegularExpressions;
using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Enums;

namespace OrientPyx.BusinessLogic.Models;

/// <summary>
/// Checks of a group's course on a day, shared by the «Групи» cells and the day readiness check so both judge a course
/// the same way.
/// </summary>
public static partial class GroupCourseChecks
{
    // A code of a course order / pattern: a run of letters and digits — not the count of an any-of block «[2: …]».
    [GeneratedRegex(@"\b[\p{L}\p{N}]+\b(?!\s*:)")]
    private static partial Regex CodeRegex();

    /// <summary>The day's control point codes (start/finish included), trimmed, compared via <see cref="TextKey"/>.</summary>
    public static IReadOnlySet<string> DayCodes(IEnumerable<ControlPoint> controlPoints) =>
        controlPoints.Select(c => c.Code.Trim()).Where(c => c.Length > 0).ToHashSet(TextKey.Comparer);

    /// <summary>
    /// The codes of <paramref name="courseOrder"/> that are no control point of the day — e.g. «S1»/«F» typed while no
    /// КП has that code. Judging treats such a code as a control nobody can punch: the whole group gets MP. Empty when
    /// the day has no control points yet (nothing to check against).
    /// </summary>
    public static IReadOnlyList<string> UnknownCodes(string? courseOrder, IReadOnlySet<string> dayCodes) =>
        dayCodes.Count == 0
            ? []
            : CodeRegex().Matches(courseOrder ?? string.Empty)
                .Select(m => m.Value)
                .Where(code => !dayCodes.Contains(code))
                .Distinct(TextKey.Comparer)
                .ToList();

    /// <summary>
    /// The group runs several courses (edited in the courses table under «Групи»), not its single order: a scatter
    /// group (its variants), or a relay whose legs take named courses (any mode but «одна дистанція групи»).
    /// </summary>
    public static bool HasSeveralCourses(IDisciplineStrategy strategy, RelayCourseMode mode) =>
        strategy.UsesCourseVariants
        || (strategy.Competitors == CompetitorModel.TeamWithLegs && mode != RelayCourseMode.Group);
}
