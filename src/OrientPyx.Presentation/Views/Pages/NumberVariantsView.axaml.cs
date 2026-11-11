using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using OrientPyx.Presentation.Controls;
using OrientPyx.Presentation.ViewModels.Pages;

namespace OrientPyx.Presentation.Views.Pages;

public partial class NumberVariantsView : UserControl
{
    private NumberVariantsViewModel? _vm;

    // Red over a variation cell that cannot be judged or handed out: no variation, or one the group lacks.
    private static readonly ISolidColorBrush ProblemBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xDC, 0x26, 0x26));

    public NumberVariantsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += (_, _) => Unsubscribe();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        Unsubscribe();
        _vm = DataContext as NumberVariantsViewModel;
        if (_vm is null)
            return;

        _vm.Localization.PropertyChanged += OnLocalizationChanged;
        BuildBands();
    }

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e) => BuildBands();

    private void BuildBands()
    {
        if (_vm is null)
            return;

        var builder = new SheetColumnBuilder(_vm.Localization)
            .Text("NumberVariants.Col.Group", nameof(NumberVariantRowViewModel.GroupName), minWidth: 120)
            .Text("NumberVariants.Col.Number", nameof(NumberVariantRowViewModel.Bib), width: 90,
                  sortPath: nameof(NumberVariantRowViewModel.BibSort))
            .Text("NumberVariants.Col.Course", nameof(NumberVariantRowViewModel.Course), minWidth: 110)
            .CellTint(nameof(NumberVariantRowViewModel.IsProblem), ProblemBrush,
                      tooltipPath: nameof(NumberVariantRowViewModel.ProblemTooltip))
            .Text("NumberVariants.Col.Team", nameof(NumberVariantRowViewModel.TeamName), minWidth: 140)
            .Text("NumberVariants.Col.Runner", nameof(NumberVariantRowViewModel.Runner), minWidth: 180)
            .Text("NumberVariants.Col.CourseOrder", nameof(NumberVariantRowViewModel.CourseOrder), minWidth: 220);

        Sheet.Bands = builder.Bands;
    }

    // Export the table as shown (visible columns, displayed rows): the VM shows the format modal and serialises; the
    // save dialog needs the window's StorageProvider, so it runs here. Mirrors MapCountsView.
    private void OnExportClick(object? sender, RoutedEventArgs e) => _ = ExportAsync();

    private async Task ExportAsync()
    {
        if (_vm is null)
            return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
            return;

        var result = await _vm.ExportAsync(Sheet.ExportView());
        if (result is null)
            return; // nothing to export or cancelled

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = _vm.Localization.Get("NumberVariants.Export"),
            SuggestedFileName = result.SuggestedFileName,
            DefaultExtension = result.Extension,
            FileTypeChoices =
            [
                new FilePickerFileType(result.Extension.ToUpperInvariant())
                {
                    Patterns = [$"*.{result.Extension}"],
                    MimeTypes = [result.MimeType]
                }
            ]
        });

        if (file is null)
            return; // save cancelled

        await _vm.FileSaver.SaveAsync(file, result.Bytes);
    }

    private void Unsubscribe()
    {
        if (_vm is not null)
            _vm.Localization.PropertyChanged -= OnLocalizationChanged;
        _vm = null;
    }
}
