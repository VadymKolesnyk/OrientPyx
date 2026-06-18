using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientDesk.BusinessLogic.Models;
using OrientDesk.Localization;

namespace OrientDesk.Presentation.ViewModels.Dialogs;

/// <summary>
/// Modal for choosing the export format of the participants table's current view: a CSV text file or an
/// Excel (.xlsx) workbook. The two are mutually exclusive radio choices (Excel default). On confirm the
/// flow opens a save dialog and writes the on-screen rows/columns in the chosen format. Mirrors the
/// <see cref="PrintSettingsViewModel"/> TaskCompletionSource pattern: callers <c>await</c>
/// <see cref="Completion"/> for the chosen format, or null on cancel/close.
/// </summary>
public sealed partial class ExportFormatViewModel : ObservableObject
{
    private readonly TaskCompletionSource<ExportFormat?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <param name="rowCount">How many rows the current view holds (shown in the modal's message).</param>
    public ExportFormatViewModel(ILocalizationService localization, int rowCount)
    {
        Localization = localization;
        RowCount = rowCount;

        Localization.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Message));
            OnPropertyChanged(nameof(ExcelLabel));
            OnPropertyChanged(nameof(CsvLabel));
        };
    }

    public ILocalizationService Localization { get; }

    public string Title => Localization.Get("Export.Title");
    public string Message => string.Format(Localization.Get("Export.Message"), RowCount);
    public string ExcelLabel => Localization.Get("Export.Format.Excel");
    public string CsvLabel => Localization.Get("Export.Format.Csv");

    public int RowCount { get; }

    /// <summary>True when Excel is selected (the default); false ⇒ CSV. The two radios are bound to this
    /// and its inverse, so picking one clears the other.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCsv))]
    private bool _isExcel = true;

    /// <summary>The CSV radio's bound state — the inverse of <see cref="IsExcel"/>.</summary>
    public bool IsCsv
    {
        get => !IsExcel;
        set => IsExcel = !value;
    }

    /// <summary>Completes with the chosen format on confirm, or null on cancel/close.</summary>
    public Task<ExportFormat?> Completion => _completion.Task;

    [RelayCommand]
    private void Confirm() => _completion.TrySetResult(IsExcel ? ExportFormat.Excel : ExportFormat.Csv);

    [RelayCommand]
    private void Cancel() => _completion.TrySetResult(null);
}
