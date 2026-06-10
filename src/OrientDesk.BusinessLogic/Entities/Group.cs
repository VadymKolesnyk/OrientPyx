namespace OrientDesk.BusinessLogic.Entities;

/// <summary>
/// A competition-level group (e.g. "М21", "Ж14", "Відкрита"). Groups belong to the whole
/// competition, not a single day; a group's presence on a given day is expressed by a
/// <see cref="GroupDaySettings"/> row referencing it.
/// </summary>
public class Group
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Display name; unique per competition (case-insensitive).</summary>
    public string Name { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}
