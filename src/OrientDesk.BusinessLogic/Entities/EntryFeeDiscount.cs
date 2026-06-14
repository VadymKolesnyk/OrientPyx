namespace OrientDesk.BusinessLogic.Entities;

/// <summary>
/// A competition-level discount that can be applied to a participant's start-entry fee, e.g.
/// "Знижка ВПО" 50%, "Знижка ЗСУ" 100% (also off chip rental), "Сімейна знижка" 30%. Belongs to the
/// whole competition. Edited on the «Стартові внески» page; no fee calculation is wired yet.
/// </summary>
public class EntryFeeDiscount
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Display name of the discount.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Discount as a percentage (0–100).</summary>
    public decimal Percent { get; set; }

    /// <summary>When true, the discount also reduces the chip-rental charge, not just the entry fee.</summary>
    public bool AppliesToChipRental { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}
