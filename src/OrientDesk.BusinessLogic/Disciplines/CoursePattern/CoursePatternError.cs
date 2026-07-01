namespace OrientDesk.BusinessLogic.Disciplines.CoursePattern;

/// <summary>
/// A structural problem found while parsing a course pattern. Layer-neutral: carries a <see cref="Kind"/>
/// (resolved to a localized message by the presentation layer) plus the offending <see cref="Token"/> to
/// splice into that message (e.g. the bracket, the bad count, or "N&gt;options").
/// </summary>
public readonly record struct CoursePatternError(CoursePatternErrorKind Kind, string Token);

/// <summary>The kinds of course-pattern parse error. Each maps to a localization key in the UI.</summary>
public enum CoursePatternErrorKind
{
    /// <summary>An unmatched <c>&lt;</c>, <c>[</c>, <c>&gt;</c> or <c>]</c> — brackets aren't balanced.</summary>
    UnbalancedBracket,

    /// <summary>A choice block <c>[…]</c> written without the required <c>:</c> after the count.</summary>
    ChoiceMissingColon,

    /// <summary>A choice block whose count before the colon is missing or not a non-negative integer.</summary>
    ChoiceBadCount,

    /// <summary>A choice block <c>[N: ]</c> with no options listed.</summary>
    EmptyChoiceBlock,

    /// <summary>A choice block whose required count exceeds the number of options (<c>N &gt; options</c>).</summary>
    ChoiceCountTooLarge,

    /// <summary>An ordered block <c>&lt;&gt;</c> with nothing inside.</summary>
    EmptyOrderedBlock
}
