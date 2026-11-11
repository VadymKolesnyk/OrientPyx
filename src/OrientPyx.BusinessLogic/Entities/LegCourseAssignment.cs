namespace OrientPyx.BusinessLogic.Entities;

/// <summary>
/// The named course of one number on a day — team <see cref="TeamNumber"/>, leg <see cref="Leg"/> («100.2») runs course
/// <see cref="CourseKey"/> (the code of one of the group's <see cref="GroupCourseVariant"/> rows). Relay
/// «за номером етапу» (<see cref="Enums.RelayCourseMode.ByBib"/>, Р-41) judges the leg against it; «набір на команду»
/// takes the team's set from its numbers' rows; a scatter group keeps the runner's map variant here (leg 1, or the
/// course-setter's legs) for «Варіанти за номерами». Keyed by the number rather than the team id so assignments imported
/// from OCAD before the teams exist still apply.
/// </summary>
public class LegCourseAssignment
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Owning <see cref="EventDay"/> id (foreign key by convention; no navigation).</summary>
    public Guid EventDayId { get; set; }

    /// <summary>The group the course belongs to, when known (set by the course import and by «Команди»; null for rows
    /// read from a file that names no group — the team of that number tells it then).</summary>
    public Guid? GroupId { get; set; }

    public int TeamNumber { get; set; }

    public int Leg { get; set; }

    /// <summary>The course code (e.g. "A").</summary>
    public string CourseKey { get; set; } = string.Empty;
}
