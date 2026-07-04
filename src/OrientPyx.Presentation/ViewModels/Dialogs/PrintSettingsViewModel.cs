using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Localization;

namespace OrientPyx.Presentation.ViewModels.Dialogs;

/// <summary>
/// Modal for choosing the split-printout target: which installed printer to print to and the thermal-roll
/// width (56 or 80 mm). Pre-fills from the stored app settings and the OS's installed-printer list; on
/// confirm it persists the choice via <see cref="IAppSettingsService"/> and completes true. When printing
/// is unsupported (non-Windows) the printer list is empty and a note is shown. Mirrors the
/// <see cref="AssignNumbersViewModel"/> TaskCompletionSource pattern.
/// </summary>
public sealed partial class PrintSettingsViewModel : ObservableObject
{
    private readonly TaskCompletionSource<bool> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly IAppSettingsService _settings;

    public PrintSettingsViewModel(
        ILocalizationService localization,
        IAppSettingsService settings,
        ISplitPrintService printService,
        PrintSettings current)
    {
        Localization = localization;
        _settings = settings;

        IsSupported = printService.IsSupported;
        foreach (var name in printService.GetInstalledPrinters())
            Printers.Add(name);
        foreach (var width in settings.ReceiptWidths)
            Widths.Add(width);

        // Pre-select the saved printer when it is still installed; else the system default (first).
        _selectedPrinter = Printers.FirstOrDefault(p => p == current.PrinterName) ?? Printers.FirstOrDefault();
        _selectedWidth = Widths.Contains(current.WidthMm) ? current.WidthMm : settings.DefaultReceiptWidth;

        Localization.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(PrinterLabel));
            OnPropertyChanged(nameof(WidthLabel));
            OnPropertyChanged(nameof(UnsupportedNote));
        };
    }

    public ILocalizationService Localization { get; }

    public string Title => Localization.Get("Print.Settings.Title");
    public string PrinterLabel => Localization.Get("Print.Settings.Printer");
    public string WidthLabel => Localization.Get("Print.Settings.Width");
    public string UnsupportedNote => Localization.Get("Print.Unsupported");

    /// <summary>False off-Windows — the view shows the unsupported note and disables saving.</summary>
    public bool IsSupported { get; }

    /// <summary>Installed printer names; empty when unsupported.</summary>
    public ObservableCollection<string> Printers { get; } = [];

    /// <summary>Allowed roll widths (mm), e.g. 56 / 80.</summary>
    public ObservableCollection<int> Widths { get; } = [];

    [ObservableProperty]
    private string? _selectedPrinter;

    [ObservableProperty]
    private int _selectedWidth;

    /// <summary>Completes true once the chosen settings are saved, false on cancel/close.</summary>
    public Task<bool> Completion => _completion.Task;

    [RelayCommand]
    private async Task Confirm()
    {
        await _settings.SavePrintSettingsAsync(new PrintSettings(SelectedPrinter ?? string.Empty, SelectedWidth));
        _completion.TrySetResult(true);
    }

    [RelayCommand]
    private void Cancel() => _completion.TrySetResult(false);
}
