namespace OrientDesk.BusinessLogic.Entities;

/// <summary>
/// A competition-level discount that can be applied to a participant's start-entry fee, e.g.
/// "Знижка ВПО" 50%, "Знижка ЗСУ" 100% (also off chip rental), "Сімейна знижка" 30%. Belongs to the
/// whole competition, edited on the «Стартові внески» page, and applied to a participant's total via
/// the per-discount columns on the participants table (largest selected percent wins).
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

    /// <summary>
    /// Marks the special, always-present discount «Знижка членам ФСОУ». It is seeded once per
    /// competition, cannot be deleted, and applies automatically to any participant whose
    /// <see cref="Participant.IsFsouMember"/> is set (rather than via a per-participant checkbox).
    /// Exactly one discount carries this flag.
    /// </summary>
    public bool IsFsouMemberDiscount { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}
