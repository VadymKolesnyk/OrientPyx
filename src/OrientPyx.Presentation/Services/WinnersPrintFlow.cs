using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Localization;
using OrientPyx.Presentation.ViewModels.Dialogs;

namespace OrientPyx.Presentation.Services;

/// <summary>
/// Default <see cref="IWinnersPrintFlow"/>. Prints the winners printout to the read-out thermal printer, reusing
/// the split-printout settings via <see cref="IAppSettingsService"/>, and forcing the print-settings modal when
/// no printer is chosen (mirrors the read-out page's print flow). Values-only document in, paper out.
/// </summary>
public sealed class WinnersPrintFlow : IWinnersPrintFlow
{
    private readonly ILocalizationService _localization;
    private readonly IAppSettingsService _appSettings;
    private readonly ISplitPrintService _printer;
    private readonly IDialogService _dialogs;

    public WinnersPrintFlow(
        ILocalizationService localization,
        IAppSettingsService appSettings,
        ISplitPrintService printer,
        IDialogService dialogs)
    {
        _localization = localization;
        _appSettings = appSettings;
        _printer = printer;
        _dialogs = dialogs;
    }

    public async Task PrintAsync(WinnersPrintDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (document.IsEmpty)
        {
            await ShowInfoAsync("Winners.Empty");
            return;
        }

        if (!_printer.IsSupported)
        {
            await ShowInfoAsync("Print.Unsupported");
            return;
        }

        // Always show the print-settings modal before printing the winners — the operator confirms/changes the
        // printer + roll width every time (this printout is issued deliberately, unlike the auto-printed slips).
        var settings = await _appSettings.GetPrintSettingsAsync();
        var saved = await _dialogs.ShowPrintSettingsAsync(
            new PrintSettingsViewModel(_localization, _appSettings, _printer, settings));
        if (!saved)
            return;
        settings = await _appSettings.GetPrintSettingsAsync();
        if (!settings.HasPrinter)
            return;

        try
        {
            await _printer.PrintWinnersAsync(document, BuildLabels(), settings);
        }
        catch (PrintNotSupportedException)
        {
            await ShowInfoAsync("Print.Unsupported");
        }
    }

    private WinnersPrintPrintLabels BuildLabels() =>
        new(HeaderTitle: _localization.Get("Winners.HeaderTitle"));

    private Task ShowInfoAsync(string messageKey) => _dialogs.ConfirmAsync(new ConfirmDialogViewModel(
        _localization,
        titleKey: "Winners.Print",
        messageKey: messageKey,
        confirmKey: "Common.Ok",
        cancelKey: "Common.Ok"));
}
