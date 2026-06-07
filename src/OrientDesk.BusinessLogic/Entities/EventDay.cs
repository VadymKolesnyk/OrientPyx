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

    /// <summary>Free-text discipline / kind of the day (e.g. "Маркована траса").</summary>
    public string Discipline { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}
