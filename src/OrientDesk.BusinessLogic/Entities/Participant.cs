namespace OrientDesk.BusinessLogic.Entities;

/// <summary>A competitor. Placeholder — only basic identity fields for now.</summary>
public class Participant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;

    /// <summary>SportIdent chip number, when assigned.</summary>
    public string? ChipNumber { get; set; }

    public Guid? GroupId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}
