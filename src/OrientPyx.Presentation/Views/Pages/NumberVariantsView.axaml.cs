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
    // The lower table's height while it is collapsed (every number has a variation), restored when it comes back.
    private GridLength _unassignedHeight = new(1, GridUnitType.Star);

    // Red over a cell that cannot be judged or handed out: no variation, one the group lacks, or a group that is not
    // the team's.
    private static readonly ISolidColorBrush ProblemBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xDC, 0x26, 0x26));

    public NumberVariantsView()
    {
        InitializeComponent();
        SheetEditing.Bind(this, Sheet, UnassignedSheet);
        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += (_, _) => Unsubscribe();
        // F8 / context menu over mismatched rows: give the numbers to the team's group.
        Sheet.RowActionLabel = rows => _vm?.MoveToHolderGroupLabel(rows);
        Sheet.RowActionRequested += (_, rows) => { if (_vm is not null) _ = _vm.MoveToHolderGroupAsync(rows); };
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        Unsubscribe();
        _vm = DataContext as NumberVariantsViewModel;
        if (_vm is null)
            return;

        _vm.Localization.PropertyChanged += OnLocalizationChanged;
        _vm.PropertyChanged += OnViewModelPropertyChanged;
        BuildBands();
        ApplyUnassignedRow();
    }

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e) => BuildBands();

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NumberVariantsViewModel.HasUnassigned))
            ApplyUnassignedRow();
    }

    // The lower row takes its share of the height only while it has rows; otherwise it collapses to nothing.
    private void ApplyUnassignedRow()
    {
        if (_vm is null)
            return;
        var row = SplitGrid.RowDefinitions[2];
        if (_vm.HasUnassigned)
        {
            row.MinHeight = 80;
            row.Height = _unassignedHeight;
        }
        else
        {
            if (!row.Height.IsAuto)
                _unassignedHeight = row.Height;
            row.MinHeight = 0;
            row.Height = GridLength.Auto;
        }
    }

    private void OnLockedEditAttempted(object? sender, SheetLockedEditEventArgs e)
    {
        if (DataContext is PageViewModelBase vm)
            _ = vm.ExplainDayLockAsync(e.DayNumber, e.Merged);
    }

    private void BuildBands()
    {
        if (_vm is null)
            return;

        // Upper table: the numbers with a variation. «Група» and «Група команди» turn red together when they differ.
        Sheet.Bands = new SheetColumnBuilder(_vm.Localization)
            .Text("NumberVariants.Col.Group", nameof(NumberVariantRowViewModel.GroupName), minWidth: 110)
            .CellTint(nameof(NumberVariantRowViewModel.GroupMismatch), ProblemBrush,
                      tooltipPath: nameof(NumberVariantRowViewModel.MismatchTooltip))
            .Text("NumberVariants.Col.HolderGroup", nameof(NumberVariantRowViewModel.HolderGroupName), minWidth: 140)
            .CellTint(nameof(NumberVariantRowViewModel.GroupMismatch), ProblemBrush,
                      tooltipPath: nameof(NumberVariantRowViewModel.MismatchTooltip))
            .Text("NumberVariants.Col.Number", nameof(NumberVariantRowViewModel.Bib), width: 90,
                  sortPath: nameof(NumberVariantRowViewModel.BibSort))
            .Combo("NumberVariants.Col.Course",
                   nameof(NumberVariantRowViewModel.VariantOptions),
                   nameof(NumberVariantRowViewModel.SelectedVariant),
                   nameof(VariantOption.Label), minWidth: 110)
            .CellTint(nameof(NumberVariantRowViewModel.IsProblem), ProblemBrush,
                      tooltipPath: nameof(NumberVariantRowViewModel.ProblemTooltip))
            .Text("NumberVariants.Col.Team", nameof(NumberVariantRowViewModel.TeamName), minWidth: 140)
            .Text("NumberVariants.Col.Runner", nameof(NumberVariantRowViewModel.Runner), minWidth: 180)
            .Text("NumberVariants.Col.CourseOrder", nameof(NumberVariantRowViewModel.CourseOrder), minWidth: 220)
            .Bands;

        // Lower table: the numbers still without a variation — the variation cell stays red until one is picked.
        UnassignedSheet.Bands = new SheetColumnBuilder(_vm.Localization)
            .Text("NumberVariants.Col.Group", nameof(NumberVariantRowViewModel.GroupName), minWidth: 110)
            .Text("NumberVariants.Col.Number", nameof(NumberVariantRowViewModel.Bib), width: 90,
                  sortPath: nameof(NumberVariantRowViewModel.BibSort))
            .Combo("NumberVariants.Col.Course",
                   nameof(NumberVariantRowViewModel.VariantOptions),
                   nameof(NumberVariantRowViewModel.SelectedVariant),
                   nameof(VariantOption.Label), minWidth: 110)
            .CellTint(nameof(NumberVariantRowViewModel.IsProblem), ProblemBrush,
                      tooltipPath: nameof(NumberVariantRowViewModel.ProblemTooltip))
            .Text("NumberVariants.Col.Team", nameof(NumberVariantRowViewModel.TeamName), minWidth: 140)
            .Text("NumberVariants.Col.Runner", nameof(NumberVariantRowViewModel.Runner), minWidth: 180)
            .Text("NumberVariants.Col.CourseOrder", nameof(NumberVariantRowViewModel.CourseOrder), minWidth: 220)
            .Bands;
    }

    // Export the upper table as shown (visible columns, displayed rows): the VM shows the format modal and serialises;
    // the save dialog needs the window's StorageProvider, so it runs here. Mirrors MapCountsView.
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
        {
            _vm.Localization.PropertyChanged -= OnLocalizationChanged;
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
        }
        _vm = null;
    }
}
