namespace OrientPyx.BusinessLogic.Models;

/// <summary>
/// The inputs needed to compute one participant's total start-entry fee. Built by the presentation /
/// service layer (which knows the participant's days, group fees, chip and the competition's discount
/// set) and handed to <see cref="Interfaces.IEntryFeeCalculator"/>. Percent lists already account for
/// the participant's selection — including the auto-applied FSOU-member discount when relevant.
/// </summary>
public sealed class EntryFeeComputation
{
    /// <summary>One entry per day the participant runs, carrying that day's base fee and chip price.</summary>
    public IReadOnlyList<EntryFeeDayInput> Days { get; init; } = [];

    /// <summary>The percents of every discount the participant has selected (entry portion).</summary>
    public IReadOnlyCollection<decimal> SelectedEntryPercents { get; init; } = [];

    /// <summary>The percents of the participant's selected discounts that also apply to chip rental.</summary>
    public IReadOnlyCollection<decimal> SelectedChipPercents { get; init; } = [];
}

/// <summary>
/// One participating day's contribution to the fee: the entry base (the group's fee, or the raised
/// fee when the participant pays it) and the chip-rental price for that day (0 when no rental chip).
/// </summary>
/// <param name="BaseFee">Group entry fee, or the raised fee when the participant pays it. 0 when unset.</param>
/// <param name="ChipPrice">Chip-rental price for the day (note override or the base price). 0 when no chip.</param>
public readonly record struct EntryFeeDayInput(decimal BaseFee, decimal ChipPrice);
