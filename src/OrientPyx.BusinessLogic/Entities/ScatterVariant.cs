namespace OrientPyx.BusinessLogic.Entities;

/// <summary>
/// One valid passing order of a scatter («розсіювання») course for a group on a specific day. A scatter
/// group has several of these rows — every runner runs one variant, and which one they took is
/// auto-detected from their read-out. Keyed by (<see cref="EventDayId"/>, <see cref="GroupId"/>); the
/// group's own <c>GroupDaySettings.CourseOrder</c> keeps the first variant for display/distance, while the
/// full set of orders lives here so a set-course string never has to encode several sequences.
/// </summary>
public class ScatterVariant
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

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}
