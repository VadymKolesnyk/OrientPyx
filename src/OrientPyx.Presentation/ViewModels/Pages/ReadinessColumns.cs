using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.Presentation.ViewModels.Pages;

/// <summary>
/// The cell «Перейти» focuses for a readiness problem: its <see cref="ReadinessIssue.Field"/> (an intent) as the target
/// page's column — a column key or a header's localization key (<see cref="Controls.SheetTable.RevealRow"/>).
/// </summary>
internal static class ReadinessColumns
{
    /// <summary>«Змагання → Дні»: the day's «Вид змагань».</summary>
    public const string Days = "CompetitionDays.Col.Discipline";

    /// <summary>The participants' day grid (its identity columns are keyed «id:&lt;header key&gt;», a field «field.&lt;key&gt;»).</summary>
    public static string? Participants(ReadinessIssue issue) => issue.Field switch
    {
        ReadinessField.Number => "id:Participants.Col.Number",
        ReadinessField.Group => "id:Participants.Col.Group",
        ReadinessField.Chip => "id:Participants.Col.Chip",
        ReadinessField.BirthDate => "id:Participants.Col.BirthDate",
        ReadinessField.StartTime => "id:Participants.Col.StartTime",
        ReadinessField.CustomField when issue.FieldKey.Length > 0 => CustomField.ColumnPrefix + issue.FieldKey,
        _ => null
    };

    /// <summary>«Групи»: the course or the type cell.</summary>
    public static string? Groups(ReadinessIssue issue) => issue.Field switch
    {
        ReadinessField.Discipline => "Groups.Col.Discipline",
        ReadinessField.CourseOrder => "Groups.Col.CourseOrder",
        _ => null
    };
}
