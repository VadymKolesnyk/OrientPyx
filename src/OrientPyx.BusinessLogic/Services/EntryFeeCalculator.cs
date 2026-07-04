using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Services;

/// <summary>
/// Default <see cref="IEntryFeeCalculator"/>. Computes a participant's total start-entry fee across
/// all the days they run, applying the single largest selected discount (max-percent, not summed) to
/// the entry portion and the largest chip-applicable discount to the chip-rental portion.
///
/// Pure: no I/O, no EF, no Avalonia — the caller gathers the inputs (which days the participant runs,
/// the per-day base fee, the per-day chip price, the selected discount percents) and this turns them
/// into a number. Shared by the day-grid and roster fee columns so the formula lives in one place.
/// </summary>
public sealed class EntryFeeCalculator : IEntryFeeCalculator
{
    public decimal Compute(EntryFeeComputation input)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Max-percent semantics: a participant with several discounts pays only the largest one.
        // Percents are clamped to [0, 100] so a stray value can never produce a negative or inflated fee.
        var entryMultiplier = Multiplier(input.SelectedEntryPercents);
        var chipMultiplier = Multiplier(input.SelectedChipPercents);

        var total = 0m;
        foreach (var day in input.Days)
        {
            total += day.BaseFee * entryMultiplier;
            total += day.ChipPrice * chipMultiplier;
        }
        return total;
    }

    // 1 - (largest percent / 100), clamped so the discount is between 0 % and 100 %.
    private static decimal Multiplier(IReadOnlyCollection<decimal> percents)
    {
        if (percents.Count == 0)
            return 1m;

        var best = 0m;
        foreach (var p in percents)
            if (p > best)
                best = p;

        if (best <= 0m)
            return 1m;
        return best >= 100m ? 0m : 1m - best / 100m;
    }
}
