using System.Globalization;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Localization;

namespace OrientPyx.Presentation.Services;

/// <summary>
/// The "Друк переможців" flow: prints a built <see cref="WinnersPrintDocument"/> to the read-out thermal printer,
/// reusing the split-printout settings (printer + roll width) saved on the app database. Loads those settings and,
/// when none is configured (or the saved printer is gone), shows the same print-settings modal the read-out page
/// uses before printing. Owns the localized captions the renderer needs and the informational dialogs (nothing
/// to print / printing unsupported). The caller (a protocol page VM) only builds the document and invokes this.
/// </summary>
public interface IWinnersPrintFlow
{
    /// <summary>
    /// Prints <paramref name="document"/> to the configured printer. Shows the settings modal first when no printer
    /// is chosen; shows an info dialog and returns when there is nothing to print or printing is unsupported. A
    /// no-op with an info dialog when the document is empty.
    /// </summary>
    Task PrintAsync(WinnersPrintDocument document);
}

/// <summary>
/// Builds the localized <see cref="WinnersPrintLabels"/> the winners builder bakes into each place's heading:
/// "1 місце" for a single place, "2 третіх" for a shared one (count + genitive ordinal). Shared by the protocol
/// page VMs so the wording is identical on the single-day and summary printouts.
/// </summary>
public static class WinnersPrintLabelsFactory
{
    public static WinnersPrintLabels Create(ILocalizationService localization) => new(
        PlaceHeading: place => string.Format(
            CultureInfo.CurrentCulture, localization.Get("Winners.PlaceHeading"), place),
        SharedPlaceHeading: (count, place) => string.Format(
            CultureInfo.CurrentCulture, localization.Get("Winners.SharedPlaceHeading"), count, Ordinal(localization, place)));

    // The genitive-plural ordinal word for a shared place ("третіх"), for places 1–3; a generic "N-х" fallback
    // for a rare shared place beyond the podium.
    private static string Ordinal(ILocalizationService localization, int place) => place switch
    {
        1 => localization.Get("Winners.Ordinal.1"),
        2 => localization.Get("Winners.Ordinal.2"),
        3 => localization.Get("Winners.Ordinal.3"),
        _ => string.Format(CultureInfo.CurrentCulture, localization.Get("Winners.Ordinal.Default"), place)
    };
}
