using OrientPyx.Plugins.Sdk.Toolkit.Patterns;

namespace OrientPyx.BusinessLogic.Models;

/// <summary>The localization key of a course pattern's parse error (its text takes the offending token as {0}) — shared
/// by the «Групи» preview and the day readiness check.</summary>
public static class CoursePatternErrors
{
    public static string KeyOf(CoursePatternErrorKind kind) => kind switch
    {
        CoursePatternErrorKind.UnbalancedBracket => "CoursePattern.Error.UnbalancedBracket",
        CoursePatternErrorKind.ChoiceMissingColon => "CoursePattern.Error.ChoiceMissingColon",
        CoursePatternErrorKind.ChoiceBadCount => "CoursePattern.Error.ChoiceBadCount",
        CoursePatternErrorKind.EmptyChoiceBlock => "CoursePattern.Error.EmptyChoiceBlock",
        CoursePatternErrorKind.ChoiceCountTooLarge => "CoursePattern.Error.ChoiceCountTooLarge",
        CoursePatternErrorKind.EmptyOrderedBlock => "CoursePattern.Error.EmptyOrderedBlock",
        _ => "CoursePattern.Error.Generic"
    };
}
