namespace OrientPyx.BusinessLogic.Disciplines.CoursePattern;

/// <summary>
/// One concrete passage order the pattern allows — a flat list of control codes in the order they must be
/// punched. A pattern with no <c>[N: …]</c> block yields exactly one; every free-choice block multiplies the
/// count (each way of picking N options × every order those N can be taken in).
/// </summary>
/// <param name="Controls">The control codes, in punching order (start/finish markers excluded).</param>
public sealed record CourseVariant(IReadOnlyList<string> Controls)
{
    /// <summary>The order as a single line, e.g. <c>"41 42 45"</c>.</summary>
    public string Text => string.Join(' ', Controls);
}

/// <summary>
/// The result of expanding a pattern into every valid passage order. Expansion is capped
/// (<see cref="CoursePatternVariants.Limit"/>) because a wide <c>[N: …]</c> block grows factorially —
/// <see cref="Truncated"/> says the list was cut short, and <see cref="TotalCount"/> is the true number of
/// orders the pattern allows (computed combinatorially, without materialising them).
/// </summary>
/// <param name="Variants">The listed orders (at most the cap).</param>
/// <param name="TotalCount">How many orders the pattern allows in total.</param>
/// <param name="Truncated">True when <see cref="TotalCount"/> exceeds what is listed.</param>
public sealed record CourseVariantSet(
    IReadOnlyList<CourseVariant> Variants, long TotalCount, bool Truncated);
