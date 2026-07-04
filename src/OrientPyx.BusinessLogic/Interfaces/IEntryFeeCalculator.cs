using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Interfaces;

/// <summary>
/// Computes a participant's total start-entry fee from the prepared inputs. A single, pure formula —
/// the largest selected discount (max-percent) applies to the entry portion and the largest
/// chip-applicable discount to the chip-rental portion, summed across the days the participant runs.
/// </summary>
public interface IEntryFeeCalculator
{
    /// <summary>Returns the participant's total fee (never negative).</summary>
    decimal Compute(EntryFeeComputation input);
}
