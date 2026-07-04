using System.Globalization;
using System.Text;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Localization;

namespace OrientPyx.Presentation.ViewModels.Pages;

/// <summary>
/// Renders an <see cref="EntryFeeBreakdown"/> into the multi-line, localized "where the sum came from"
/// text shown when hovering a participant's total-fee cell. Lives in Presentation (it needs
/// localization, which the business layer must not reference) and is shared by both participant row
/// view models so the day grid and the roster explain the fee identically.
/// </summary>
internal static class EntryFeeBreakdownFormatter
{
    public static string Format(EntryFeeBreakdown breakdown, ILocalizationService loc)
    {
        if (breakdown.Days.Count == 0)
            return loc.Get("Fee.Breakdown.Empty");

        var sb = new StringBuilder();
        for (var i = 0; i < breakdown.Days.Count; i++)
        {
            var day = breakdown.Days[i];
            var entryKey = breakdown.UsesRaisedFee ? "Fee.Breakdown.DayEntryRaised" : "Fee.Breakdown.DayEntry";
            sb.Append(loc.Get(entryKey)
                .Replace("{0}", (i + 1).ToString(CultureInfo.InvariantCulture))
                .Replace("{1}", Money(day.BaseFee)));

            switch (day.ChipReason)
            {
                case ChipRentalReason.NoChipCharged:
                    sb.Append(loc.Get("Fee.Breakdown.ChipNoChip").Replace("{0}", Money(day.ChipPrice)));
                    break;
                case ChipRentalReason.RentalChipCharged:
                    sb.Append(loc.Get("Fee.Breakdown.ChipRental").Replace("{0}", Money(day.ChipPrice)));
                    break;
                case ChipRentalReason.OwnChipNotCharged:
                    sb.Append(loc.Get("Fee.Breakdown.ChipOwn"));
                    break;
            }

            sb.AppendLine();
        }

        if (breakdown.EntryDiscountPercent > 0m)
            sb.AppendLine(loc.Get("Fee.Breakdown.EntryDiscount").Replace("{0}", Money(breakdown.EntryDiscountPercent)));
        if (breakdown.ChipDiscountPercent > 0m)
            sb.AppendLine(loc.Get("Fee.Breakdown.ChipDiscount").Replace("{0}", Money(breakdown.ChipDiscountPercent)));

        sb.Append(loc.Get("Fee.Breakdown.Total").Replace("{0}", Money(breakdown.Total)));
        return sb.ToString();
    }

    // Same money format the cells use: no currency symbol, trims trailing zeros.
    private static string Money(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
