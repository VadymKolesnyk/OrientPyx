namespace OrientDesk.BusinessLogic.Entities;

/// <summary>A rented SportIdent chip handed to a participant. Placeholder.</summary>
public class ChipRental
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ChipNumber { get; set; } = string.Empty;
    public Guid? ParticipantId { get; set; }
    public bool IsReturned { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}
