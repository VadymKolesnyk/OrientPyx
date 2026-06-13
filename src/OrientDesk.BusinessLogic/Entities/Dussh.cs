namespace OrientDesk.BusinessLogic.Entities;

/// <summary>
/// A competition-level sports school (ДЮСШ — дитячо-юнацька спортивна школа) a participant attends.
/// Like <see cref="Region"/> and <see cref="Club"/> it belongs to the whole competition, not a
/// single day; a participant references one optionally via <see cref="Participant.DusshId"/>. The
/// name is unique per competition (case-insensitive).
/// </summary>
public class Dussh
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Display name; unique per competition (case-insensitive).</summary>
    public string Name { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}
