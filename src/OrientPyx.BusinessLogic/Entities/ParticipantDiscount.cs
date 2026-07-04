namespace OrientPyx.BusinessLogic.Entities;

/// <summary>
/// A join row marking that a participant gets a particular <see cref="EntryFeeDiscount"/>. The mere
/// existence of this row means "this participant qualifies for this discount"; deleting it removes
/// the discount. Discounts are competition-level, so this link is too (not per-day).
///
/// The auto-applied FSOU-member discount (<see cref="EntryFeeDiscount.IsFsouMemberDiscount"/>) is
/// NEVER stored here — it is derived from <see cref="Participant.IsFsouMember"/> at calculation time.
/// </summary>
public class ParticipantDiscount
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Owning <see cref="Participant"/> id (foreign key by convention; no navigation).</summary>
    public Guid ParticipantId { get; set; }

    /// <summary>The <see cref="EntryFeeDiscount"/> that applies (foreign key by convention; no navigation).</summary>
    public Guid DiscountId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}
