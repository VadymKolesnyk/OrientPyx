using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Localization;

namespace OrientPyx.Presentation.ViewModels.Dialogs;

/// <summary>
/// Modal for choosing the A4 print target (the participant statement / «відомість»): which installed printer to
/// print to. Paper is always A4, so no size is offered. Pre-fills from the stored app settings and the OS's
/// installed-printer list; on confirm it persists the choice via <see cref="IAppSettingsService"/> and completes
/// true. When printing is unsupported (non-Windows) the printer list is empty and a note is shown. Mirrors
/// <see cref="PrintSettingsViewModel"/>.
/// </summary>
public sealed partial class A4PrintSettingsViewModel : ObservableObject
{
    private readonly TaskCompletionSource<bool> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly IAppSettingsService _settings;

    public A4PrintSettingsViewModel(
        ILocalizationService localization,
        IAppSettingsService settings,
        IStatementPrintService printService,
        A4PrintSettings current)
    {
        Localization = localization;
        _settings = settings;

        IsSupported = printService.IsSupported;
        foreach (var name in printService.GetInstalledPrinters())
            Printers.Add(name);

        _selectedPrinter = Printers.FirstOrDefault(p => p == current.PrinterName) ?? Printers.FirstOrDefault();

        Localization.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(PrinterLabel));
            OnPropertyChanged(nameof(UnsupportedNote));
        };
    }

    public ILocalizationService Localization { get; }

    public string Title => Localization.Get("A4Print.Settings.Title");
    public string PrinterLabel => Localization.Get("Print.Settings.Printer");
    public string UnsupportedNote => Localization.Get("Print.Unsupported");

    /// <summary>False off-Windows — the view shows the unsupported note and disables saving.</summary>
    public bool IsSupported { get; }

    /// <summary>Installed printer names; empty when unsupported.</summary>
    public ObservableCollection<string> Printers { get; } = [];

    [ObservableProperty]
    private string? _selectedPrinter;

    /// <summary>Completes true once the chosen printer is saved, false on cancel/close.</summary>
    public Task<bool> Completion => _completion.Task;

    [RelayCommand]
    private async Task Confirm()
    {
        await _settings.SaveA4PrintSettingsAsync(new A4PrintSettings(SelectedPrinter ?? string.Empty));
        _completion.TrySetResult(true);
    }

    [RelayCommand]
    private void Cancel() => _completion.TrySetResult(false);
}
