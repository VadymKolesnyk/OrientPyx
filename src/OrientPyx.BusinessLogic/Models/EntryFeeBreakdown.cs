namespace OrientPyx.BusinessLogic.Models;

/// <summary>
/// A structured, layer-agnostic explanation of how a participant's total start-entry fee was reached:
/// the per-day entry and chip-rental contributions plus the single discount that was applied to each
/// portion. Carries numbers and flags only (no localized text) so the presentation layer can render a
/// human-readable "where the sum came from" tooltip without the business layer depending on
/// localization. Produced by <see cref="Services.EntryFeeContext"/> alongside the total.
/// </summary>
public sealed class EntryFeeBreakdown
{
    /// <summary>One line per day the participant runs, in the order the days were supplied.</summary>
    public IReadOnlyList<EntryFeeDayBreakdown> Days { get; init; } = [];

    /// <summary>The largest selected discount percent applied to the entry portion (0 = none).</summary>
    public decimal EntryDiscountPercent { get; init; }

    /// <summary>The largest selected discount percent applied to the chip-rental portion (0 = none).</summary>
    public decimal ChipDiscountPercent { get; init; }

    /// <summary>Whether the raised (late) fee replaced the group fee on every day.</summary>
    public bool UsesRaisedFee { get; init; }

    /// <summary>The final total after discounts — equal to <see cref="Interfaces.IEntryFeeCalculator"/>.</summary>
    public decimal Total { get; init; }

    /// <summary>
    /// The share of <see cref="Total"/> that one specific day contributes — its entry base and chip price
    /// with the same discount multipliers applied. Because a discount is a plain multiplier, the day shares
    /// sum exactly to <see cref="Total"/>. 0 when the participant does not run that day.
    /// </summary>
    public decimal DayTotal(Guid dayId)
    {
        var entryMultiplier = Multiplier(EntryDiscountPercent);
        var chipMultiplier = Multiplier(ChipDiscountPercent);
        var sum = 0m;
        foreach (var day in Days)
            if (day.DayId == dayId)
                sum += day.BaseFee * entryMultiplier + day.ChipPrice * chipMultiplier;
        return sum;
    }

    // Mirrors EntryFeeCalculator.Multiplier for a single already-resolved (largest) percent.
    private static decimal Multiplier(decimal percent) =>
        percent <= 0m ? 1m : percent >= 100m ? 0m : 1m - percent / 100m;
}

/// <summary>
/// One day's contribution to the breakdown: the pre-discount entry base and chip price, plus why the
/// chip price is what it is. The presentation layer turns this into a line like
/// "День 2: внесок 150 + оренда чипа 30".
/// </summary>
/// <param name="DayId">Which day this line is for, so a caller can ask what one specific day costs
/// (per-day payment mode compares each day's payment against its own share). <see cref="Guid.Empty"/>
/// when the caller supplied no day id.</param>
/// <param name="BaseFee">The day's entry base before discount (group fee or raised fee). 0 when unset.</param>
/// <param name="ChipPrice">The day's chip-rental price before discount. 0 when no rental is charged.</param>
/// <param name="ChipReason">Why a chip-rental price was (or was not) charged this day.</param>
public readonly record struct EntryFeeDayBreakdown(Guid DayId, decimal BaseFee, decimal ChipPrice, ChipRentalReason ChipReason);

/// <summary>
/// Why a day's chip-rental price was charged or skipped — so the tooltip can say e.g. "chip not
/// specified" vs "own chip, no rental". Rental is charged when no chip is specified at all, or when the
/// chip is one of the organiser's rental chips; an own (non-rental) chip is never charged.
/// </summary>
public enum ChipRentalReason
{
    /// <summary>No chip was specified, so a rental chip is assumed and charged.</summary>
    NoChipCharged,

    /// <summary>The chip is one of the organiser's rental chips, so rental is charged.</summary>
    RentalChipCharged,

    /// <summary>The chip is the participant's own (not in the rental pool), so nothing is charged.</summary>
    OwnChipNotCharged,
}
