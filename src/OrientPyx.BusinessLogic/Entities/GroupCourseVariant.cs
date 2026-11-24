namespace OrientPyx.BusinessLogic.Entities;

/// <summary>
/// One valid passing order of a scatter («розсіювання») course for a group on a specific day. A scatter
/// group has several of these rows — every runner runs one variant, and which one they took is
/// auto-detected from their read-out. Keyed by (<see cref="EventDayId"/>, <see cref="GroupId"/>); the
/// group's own <c>GroupDaySettings.CourseOrder</c> keeps the first variant for display/distance, while the
/// full set of orders lives here so a set-course string never has to encode several sequences.
/// </summary>
public class GroupCourseVariant
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Owning <see cref="EventDay"/> id (foreign key by convention; no navigation).</summary>
    public Guid EventDayId { get; set; }

    /// <summary>Owning <see cref="Group"/> id (foreign key by convention; no navigation).</summary>
    public Guid GroupId { get; set; }

    /// <summary>Short display code identifying the variant, e.g. "A", "B" (from the file or assigned A/B/…).</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>
    /// Control-point order for this variant as a raw free string, e.g. "S1 31 32 33 F" (start/finish kept),
    /// mirroring <c>GroupDaySettings.CourseOrder</c>; reduced to the required controls at read time via the
    /// shared course-order helpers.
    /// </summary>
    public string CourseOrder { get; set; } = string.Empty;

    /// <summary>Stable display/sort order of the variant within the group (A before B before …).</summary>
    public int Order { get; set; }

    /// <summary>Relay «своя на кожен етап» (<c>RelayCourseMode.PerLeg</c>): the leg this course is run on (1-based);
    /// several courses may share a leg — the runner gets the one their read-out matches. Null = no leg. Kept in every
    /// other mode (only that mode reads it), so switching modes loses nothing.</summary>
    public int? Leg { get; set; }

    /// <summary>The course / variation name exactly as the course-setting file wrote it (IOF 3.0 <c>Course/Name</c>,
    /// 2.0.3 variation name), so the IOF export can name the runner's course as the file did — for a renamed or split
    /// group too. Null for a variant typed by hand or imported before this was kept.</summary>
    public string? SourceCourseName { get; set; }

    /// <summary>The course family as the file wrote it (IOF 3.0 <c>CourseFamily</c>, 2.0.3 course name); null when
    /// the file named none.</summary>
    public string? SourceCourseFamily { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}
