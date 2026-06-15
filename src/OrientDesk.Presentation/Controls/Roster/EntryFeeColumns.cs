using System.Collections.Generic;
using OrientDesk.BusinessLogic.Entities;
using OrientDesk.Localization;

namespace OrientDesk.Presentation.Controls;

/// <summary>
/// Builds the shared entry-fee "tail" columns appended to the end of the participants table by both
/// <see cref="DayColumnBuilder"/> and <see cref="RosterColumnBuilder"/>: an optional raised-fee flag,
/// one checkbox column per discount (including the auto-applied FSOU-member discount), and a read-only
/// total. Kept in one place so the two builders stay in step.
/// </summary>
internal static class EntryFeeColumns
{
    /// <summary>
    /// Appends the entry-fee bands to <paramref name="bands"/> (just before the trailing actions band).
    /// The raised-fee flag is added only when <paramref name="raisedFeeEnabled"/> is true. Discount
    /// columns are headed by the discount's own name; the FSOU-member discount's checkbox is disabled
    /// (it follows «Член ФСОУ» rather than being clicked). Columns key off the discount id so a hidden
    /// set survives rebuilds.
    /// </summary>
    public static void Append(
        List<SheetBand> bands,
        ILocalizationService loc,
        IReadOnlyList<EntryFeeDiscount> discounts,
        bool raisedFeeEnabled)
    {
        if (raisedFeeEnabled)
        {
            var raised = new SheetColumn(SheetCellKind.RaisedFeeFlag)
            {
                Header = loc.Get("Participants.Col.RaisedFee"),
                Width = 120,
                WidthCapped = true,
                Key = "fee:raised",
                PickerLabel = loc.Get("Participants.Col.RaisedFee"),
            };
            bands.Add(new SheetBand(SheetBand.BandKind.Identity, [raised]) { Header = raised.Header });
        }

        for (var i = 0; i < discounts.Count; i++)
        {
            var discount = discounts[i];
            // The FSOU-member discount is applied automatically off «Член ФСОУ»; its own column would
            // just be a permanently-disabled duplicate of that checkbox, so we don't show it.
            if (discount.IsFsouMemberDiscount)
            {
                continue;
            }

            var index = i;
            var label = string.IsNullOrWhiteSpace(discount.Name)
                ? loc.Get("EntryFees.Discount.Unnamed")
                : discount.Name;
            var col = new SheetColumn(SheetCellKind.Custom)
            {
                Header = label,
                Width = 120,
                WidthCapped = true,
                // Stable key by discount id, so hiding a discount column survives a rebuild / rename.
                Key = $"fee:discount:{discount.Id}",
                PickerLabel = label,
                CellBuilder = () => RosterCellFactory.BuildDiscountFlag(index, enabled: !discount.IsFsouMemberDiscount),
            };
            bands.Add(new SheetBand(SheetBand.BandKind.Identity, [col]) { Header = label });
        }

        var total = new SheetColumn(SheetCellKind.TotalFee)
        {
            Header = loc.Get("Participants.Col.TotalFee"),
            Width = 130,
            WidthCapped = true,
            Key = "fee:total",
            PickerLabel = loc.Get("Participants.Col.TotalFee"),
            // Both row VMs expose a numeric TotalEntryFee used for sorting/filtering.
            SortPath = "TotalEntryFee",
        };
        bands.Add(new SheetBand(SheetBand.BandKind.Identity, [total]) { Header = total.Header });
    }
}
