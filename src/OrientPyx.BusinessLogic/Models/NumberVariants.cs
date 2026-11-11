namespace OrientPyx.BusinessLogic.Models;

/// <summary>
/// «Варіанти за номерами»: which course variation (map) each number runs on the day — a relay leg number «101.2» or a
/// scatter runner's bib — as the course-setting program handed them out (the OCAD import) or as typed on «Команди».
/// Read-only; built by <c>ICompetitionEditorService.GetNumberVariantsAsync</c> for the session's day.
/// </summary>
public sealed record NumberVariantsData(IReadOnlyList<NumberVariantRow> Rows)
{
    public static readonly NumberVariantsData Empty = new([]);
}

/// <summary>
/// One number and leg: its group, the bib as printed («101.2» on a relay leg or a multi-leg number, «101» otherwise),
/// the variation code and its control order, and who runs it (the team and the leg's runner, or the scatter runner).
/// </summary>
/// <param name="GroupId">The group, when known (null: no group of the day claims the number).</param>
/// <param name="Course">The variation code; blank when the leg number has none (<see cref="Missing"/>).</param>
/// <param name="CourseOrder">The variation's controls; blank when the code is not one of the group's variants.</param>
/// <param name="Missing">A relay leg number «за номером етапу» with no variation — the leg cannot be judged.</param>
/// <param name="UnknownCourse">The code names no variant of the group (a file or a typo the variants don't have).</param>
public sealed record NumberVariantRow(
    Guid? GroupId,
    string GroupName,
    int Number,
    int Leg,
    string Bib,
    string Course,
    string CourseOrder,
    string TeamName,
    string Runner,
    bool Missing,
    bool UnknownCourse);
