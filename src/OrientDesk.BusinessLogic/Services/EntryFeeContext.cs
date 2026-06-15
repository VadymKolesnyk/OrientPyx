using OrientDesk.BusinessLogic.Entities;
using OrientDesk.BusinessLogic.Interfaces;
using OrientDesk.BusinessLogic.Models;

namespace OrientDesk.BusinessLogic.Services;

/// <summary>
/// A snapshot of a competition's entry-fee inputs (raised fee, chip prices + note overrides, the
/// discount set and their percents, rental-chip notes) plus the logic to turn one participant's
/// state into a total fee. Built once from the loaded lists and reused per participant — pure given
/// the snapshot. Shared by <see cref="CompetitionEditorService"/> (precomputes the column on load)
/// and the presentation row view models (live recompute on a toggle, no DB round-trip).
/// </summary>
public sealed class EntryFeeContext
{
    private readonly IEntryFeeCalculator _calc;
    private readonly decimal _raisedFee;
    private readonly bool _raisedFeeEnabled;
    private readonly decimal _chipBasePrice;
    private readonly Dictionary<Guid, decimal> _groupFee;
    private readonly Dictionary<string, decimal> _chipPriceByNote;
    private readonly decimal _fsouPercent;
    private readonly Dictionary<Guid, (decimal Percent, bool AppliesToChip)> _discountById;
    private readonly Dictionary<string, string> _noteByChip;

    public EntryFeeContext(
        IEntryFeeCalculator calc,
        CompetitionInfo? info,
        IReadOnlyList<Group> groups,
        IReadOnlyList<ChipPriceOverride> chipPrices,
        IReadOnlyList<EntryFeeDiscount> discounts,
        IReadOnlyList<RentalChip> rentalChips)
    {
        _calc = calc;
        _raisedFeeEnabled = info?.RaisedFeeEnabled ?? false;
        _raisedFee = info?.RaisedFeeAmount ?? 0m;
        _chipBasePrice = info?.ChipRentalPricePerDay ?? 0m;
        _groupFee = groups.ToDictionary(g => g.Id, g => g.EntryFee ?? 0m);
        // A chip note → price/day; last write wins on a duplicate note (the page sorts by note).
        _chipPriceByNote = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in chipPrices)
            _chipPriceByNote[o.Note.Trim()] = o.PricePerDay;
        _fsouPercent = discounts.FirstOrDefault(d => d.IsFsouMemberDiscount)?.Percent ?? 0m;
        _discountById = discounts
            .Where(d => !d.IsFsouMemberDiscount)
            .ToDictionary(d => d.Id, d => (d.Percent, d.AppliesToChipRental));
        // Chip number → its rental note, so a participant's chip resolves to a price override.
        _noteByChip = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in rentalChips)
        {
            var num = c.Number.Trim();
            if (num.Length > 0)
                _noteByChip[num] = c.Note ?? string.Empty;
        }
    }

    /// <summary>
    /// Computes a participant's total entry fee over the days they run. The raised fee (when enabled
    /// and the participant pays it) replaces the group fee per day. The single largest selected
    /// discount applies to the entry portion (including the FSOU-member discount when the participant
    /// is a member); the largest chip-applicable discount applies to the chip portion.
    /// </summary>
    /// <param name="paysRaisedFee">The participant's "pays the raised fee" flag.</param>
    /// <param name="isFsouMember">Whether the participant is an FSOU member (auto-applies that discount).</param>
    /// <param name="selectedDiscountIds">The manual (non-FSOU) discount ids the participant has selected.</param>
    /// <param name="memberDays">Each day the participant runs: its group id (null = no group) and chip.</param>
    public decimal Total(
        bool paysRaisedFee,
        bool isFsouMember,
        IEnumerable<Guid> selectedDiscountIds,
        IEnumerable<(Guid? GroupId, string Chip)> memberDays)
    {
        var useRaised = _raisedFeeEnabled && paysRaisedFee;

        var days = new List<EntryFeeDayInput>();
        foreach (var (groupId, chip) in memberDays)
        {
            var baseFee = useRaised
                ? _raisedFee
                : groupId is { } gid && _groupFee.TryGetValue(gid, out var f) ? f : 0m;
            days.Add(new EntryFeeDayInput(baseFee, ChipPriceFor(chip)));
        }

        var entryPercents = new List<decimal>();
        var chipPercents = new List<decimal>();
        foreach (var id in selectedDiscountIds)
        {
            if (!_discountById.TryGetValue(id, out var d))
                continue;
            entryPercents.Add(d.Percent);
            if (d.AppliesToChip)
                chipPercents.Add(d.Percent);
        }
        // The FSOU-member discount applies to the entry portion only (it is the membership discount on
        // the fee, with no chip-rental concept).
        if (isFsouMember && _fsouPercent > 0m)
            entryPercents.Add(_fsouPercent);

        return _calc.Compute(new EntryFeeComputation
        {
            Days = days,
            SelectedEntryPercents = entryPercents,
            SelectedChipPercents = chipPercents
        });
    }

    // Chip price for a day: the override for the chip's note, else the competition base price. A blank
    // chip means no rental that day (0). A chip with no rental record uses the base price.
    private decimal ChipPriceFor(string? chip)
    {
        var num = (chip ?? string.Empty).Trim();
        if (num.Length == 0)
            return 0m;
        if (_noteByChip.TryGetValue(num, out var note))
        {
            var key = note.Trim();
            if (key.Length > 0 && _chipPriceByNote.TryGetValue(key, out var price))
                return price;
        }
        return _chipBasePrice;
    }
}
