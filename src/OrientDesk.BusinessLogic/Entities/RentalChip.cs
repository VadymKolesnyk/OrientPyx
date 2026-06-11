namespace OrientDesk.BusinessLogic.Entities;

/// <summary>
/// A rental chip held by the organiser and issued to participants for the whole competition
/// (not a single day). The chip is identified by its <see cref="Number"/>, which is unique per
/// competition so it can be joined against participant entries later.
/// </summary>
public class RentalChip
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Chip number as printed on the chip, e.g. "9007400". Unique per competition.</summary>
    public string Number { get; set; } = string.Empty;

    /// <summary>Free-text note about the chip (condition, owner, batch, …). Optional.</summary>
    public string Note { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}
