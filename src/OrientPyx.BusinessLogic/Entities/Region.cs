namespace OrientPyx.BusinessLogic.Entities;

/// <summary>
/// A competition-level region (the place/club a participant comes from, e.g. "Київ", "Львів").
/// Regions belong to the whole competition, not a single day; a participant references one optionally
/// via <see cref="Participant.RegionId"/>. The name is unique per competition (case-insensitive).
/// </summary>
public class Region
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Display name; unique per competition (case-insensitive).</summary>
    public string Name { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}
