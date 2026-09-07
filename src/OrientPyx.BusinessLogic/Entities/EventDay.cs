using OrientPyx.BusinessLogic.Enums;

namespace OrientPyx.BusinessLogic.Entities;

/// <summary>A single competition day. Stored in the event database.</summary>
public class EventDay
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>1-based day number within the competition.</summary>
    public int Number { get; set; }

    /// <summary>Optional calendar date of the day.</summary>
    public DateTimeOffset? Date { get; set; }

    /// <summary>Per-day venue / location (may differ from the competition's).</summary>
    public string Venue { get; set; } = string.Empty;

    /// <summary>Default competition type for all groups on this day (groups may override later).</summary>
    public DisciplineType DefaultDiscipline { get; set; } = DisciplineType.SetCourse;

    /// <summary>
    /// True when the day is closed for editing. A locked day still reads, computes, prints and
    /// publishes as usual — only writes to its data (day membership, group, chip, start time,
    /// status, bonus, read-outs) are refused, so finished days can't be changed by accident while
    /// working on another one. Toggled from the day selector on any per-day page.
    /// </summary>
    public bool IsLocked { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}
