namespace OrientPyx.BusinessLogic.Entities;

/// <summary>
/// Relay «за номером» (<see cref="Enums.RelayCourseMode.ByBib"/>, Р-41): the named course of one leg number on a
/// day — team <see cref="TeamNumber"/>, leg <see cref="Leg"/> («100.2») runs course <see cref="CourseKey"/> (the
/// code of one of the group's <see cref="GroupCourseVariant"/> rows). Keyed by the team number rather than the team
/// id so assignments imported from OCAD before the teams exist still apply.
/// </summary>
public class LegCourseAssignment
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Owning <see cref="EventDay"/> id (foreign key by convention; no navigation).</summary>
    public Guid EventDayId { get; set; }

    public int TeamNumber { get; set; }

    public int Leg { get; set; }

    /// <summary>The course code (e.g. "A").</summary>
    public string CourseKey { get; set; } = string.Empty;
}
