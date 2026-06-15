namespace OrientDesk.BusinessLogic.Entities;

/// <summary>
/// A competition-level rule that overrides the rental-chip price per day for chips carrying a given
/// note (примітка), e.g. note "air" → 50 ₴/day while the default is 20 ₴/day. Belongs to the whole
/// competition, not a single day. Edited on the «Стартові внески» page and applied per member day in
/// the participant fee total when the participant's chip carries the matching note.
/// </summary>
public class ChipPriceOverride
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The chip note this rule matches (matched against <see cref="RentalChip.Note"/>).</summary>
    public string Note { get; set; } = string.Empty;

    /// <summary>Rental price per day for chips with the matching note.</summary>
    public decimal PricePerDay { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}
