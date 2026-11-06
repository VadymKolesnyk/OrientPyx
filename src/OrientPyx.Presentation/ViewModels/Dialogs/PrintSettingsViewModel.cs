using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Localization;
using OrientPyx.Presentation.Services;

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
    private readonly ISplitPrintService _printService;

    public PrintSettingsViewModel(
        ILocalizationService localization,
        IAppSettingsService settings,
        ISplitPrintService printService,
        PrintSettings current)
    {
        Localization = localization;
        _settings = settings;
        _printService = printService;

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
            OnPropertyChanged(nameof(TestPrintLabel));
        };
    }

    public ILocalizationService Localization { get; }

    public string Title => Localization.Get("Print.Settings.Title");
    public string PrinterLabel => Localization.Get("Print.Settings.Printer");
    public string WidthLabel => Localization.Get("Print.Settings.Width");
    public string UnsupportedNote => Localization.Get("Print.Unsupported");
    public string TestPrintLabel => Localization.Get("Print.Settings.Test");

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

    /// <summary>Outcome of the last test print (sent / failed), shown under the button; blank until one runs.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TestPrintSucceeded))]
    private string _testPrintStatus = string.Empty;

    /// <summary>True when the last test print threw (the status line turns red).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TestPrintSucceeded))]
    private bool _testPrintFailed;

    /// <summary>True while a test slip is being sent to the spooler (disables the button).</summary>
    [ObservableProperty]
    private bool _isTestPrinting;

    /// <summary>The last test print was sent without error (shows the muted status line).</summary>
    public bool TestPrintSucceeded => !TestPrintFailed && TestPrintStatus.Length > 0;

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

    // Prints the fixed sample slip (SplitPrintSample) to the printer/width currently chosen in the dialog —
    // not the saved ones — so the operator can check a new choice before saving it. Nothing is persisted.
    [RelayCommand]
    private async Task TestPrintAsync()
    {
        if (!IsSupported || string.IsNullOrWhiteSpace(SelectedPrinter) || IsTestPrinting)
            return;

        var target = new PrintSettings(SelectedPrinter, SelectedWidth);
        IsTestPrinting = true;
        try
        {
            await _printService.PrintAsync(SplitPrintSample.Create(), SplitPrintLabelsFactory.Create(Localization), target);
            TestPrintFailed = false;
            TestPrintStatus = string.Format(Localization.Get("Print.Settings.TestDone"), target.PrinterName);
        }
        catch (Exception ex)
        {
            TestPrintFailed = true;
            TestPrintStatus = string.Format(Localization.Get("Print.Settings.TestFailed"), ex.Message);
        }
        finally
        {
            IsTestPrinting = false;
        }
    }
}
