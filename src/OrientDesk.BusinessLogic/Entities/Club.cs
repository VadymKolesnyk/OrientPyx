namespace OrientDesk.BusinessLogic.Entities;

/// <summary>
/// A competition-level club (the sports club a participant belongs to). Clubs belong to the whole
/// competition, not a single day; a participant references one optionally via
/// <see cref="Participant.ClubId"/>. The name is unique per competition (case-insensitive).
/// </summary>
public class Club
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Display name; unique per competition (case-insensitive).</summary>
    public string Name { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}
