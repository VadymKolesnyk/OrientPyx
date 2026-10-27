using OrientPyx.Plugins.Sdk.Toolkit.Patterns;

namespace OrientPyx.Plugins.Sdk;

/// <summary>
/// One named course of a group on a day (Р-40) — a scatter («розсіювання») variant or a relay leg course —
/// reduced to what a discipline strategy needs to judge it: a display <see cref="Code"/> (e.g. "A"), the
/// required <see cref="Controls"/> (the course order stripped of start/finish markers and the day's disabled
/// «проблемні» controls, exactly like <c>FinishContext.ExpectedControls</c>) and the raw
/// <see cref="CourseOrderText"/> as entered. A plain row of codes is a prescribed order; a text with
/// <c>&lt;…&gt;</c> / <c>[N: …]</c> blocks is a «mixed» pattern (<see cref="IsPattern"/>).
/// Carried on <see cref="FinishContext"/> and <see cref="SplitsContext"/>.
/// </summary>
public sealed record CourseVariantData(string Code, IReadOnlyList<string> Controls, string CourseOrderText = "")
{
    /// <summary>True when the course is a «mixed» pattern rather than a plain order.</summary>
    public bool IsPattern => IsPatternText(CourseOrderText);

    /// <summary>True when <paramref name="courseOrder"/> uses pattern syntax (an ordered run or an any-of block).</summary>
    public static bool IsPatternText(string? courseOrder) =>
        courseOrder is not null && courseOrder.IndexOfAny(['<', '[']) >= 0;

    /// <summary>The parsed pattern of <see cref="CourseOrderText"/> (a plain order parses as one ordered run).</summary>
    public CoursePattern Pattern() => CoursePattern.Parse(CourseOrderText);
}
