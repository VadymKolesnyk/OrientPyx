using OrientDesk.BusinessLogic.Enums;

namespace OrientDesk.BusinessLogic.Entities;

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

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}
