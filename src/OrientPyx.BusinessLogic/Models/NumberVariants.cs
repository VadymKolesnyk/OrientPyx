namespace OrientPyx.BusinessLogic.Models;

/// <summary>
/// «Варіанти за номерами»: which course variation (map) each number runs on the day — a relay leg number «101.2» or a
/// scatter runner's bib — as the course-setting program handed them out (the OCAD import) or as typed on «Команди» or
/// on this page. Built by <c>ICompetitionEditorService.GetNumberVariantsAsync</c> for the session's day; written back
/// through <c>SetLegCoursesAsync</c>.
/// </summary>
/// <param name="Rows">Every number and leg that has a variation (one per stored assignment).</param>
/// <param name="Unassigned">
/// The numbers that should have a variation and have none: every leg of a numbered team in a relay group that takes its
/// courses by number («за номером етапу», or «набір на команду» without a set of its own), and every numbered runner of
/// a group whose other numbers have variations (a scatter handed out by number).
/// </param>
/// <param name="Groups">The day's groups with their variations — what a number of the group may be given.</param>
public sealed record NumberVariantsData(
    IReadOnlyList<NumberVariantRow> Rows,
    IReadOnlyList<NumberVariantRow> Unassigned,
    IReadOnlyList<NumberVariantGroup> Groups)
{
    public static readonly NumberVariantsData Empty = new([], [], []);

    /// <summary>Some group of the day runs a relay; without one the numbers are scatter runners' and the page drops the
    /// team columns.</summary>
    public bool HasRelayGroups { get; init; }
}

/// <summary>A group of the day and its variations, in their order.</summary>
public sealed record NumberVariantGroup(Guid GroupId, string Name, IReadOnlyList<NumberVariantCode> Variants);

/// <summary>One variation of a group: its code and control order.</summary>
public sealed record NumberVariantCode(string Code, string CourseOrder);

/// <summary>
/// One number and leg: its group, the group of whoever holds the number, the bib as printed («101.2» on a relay leg or
/// a multi-leg number, «101» otherwise), the variation code and its control order, and who runs it (the team and the
/// leg's runner, or the scatter runner).
/// </summary>
/// <param name="GroupId">
/// The group the variation belongs to: the one the assignment names, else the holder's (null: neither is known).
/// </param>
/// <param name="HolderGroupId">
/// The group of the team holding the number (a relay) or of the runner with that bib (null: nobody holds it).
/// </param>
/// <param name="Course">The variation code; blank when the number has none (<see cref="Missing"/>).</param>
/// <param name="CourseOrder">The variation's controls; blank when the code is not one of the group's variants.</param>
/// <param name="Missing">The number has no variation (an <see cref="NumberVariantsData.Unassigned"/> row).</param>
/// <param name="UnknownCourse">The code names no variant of the group (a file or a typo the variants don't have).</param>
public sealed record NumberVariantRow(
    Guid? GroupId,
    string GroupName,
    Guid? HolderGroupId,
    string HolderGroupName,
    int Number,
    int Leg,
    string Bib,
    string Course,
    string CourseOrder,
    string TeamName,
    string Runner,
    bool Missing,
    bool UnknownCourse)
{
    /// <summary>
    /// The variation names one group and the number is held in another: the results never apply it (a team only takes
    /// the rows of its own group), so the leg is left without a variation.
    /// </summary>
    public bool GroupMismatch => GroupId is { } own && HolderGroupId is { } holder && own != holder;
}
