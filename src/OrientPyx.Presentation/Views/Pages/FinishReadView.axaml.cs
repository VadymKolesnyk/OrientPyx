using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using OrientPyx.Presentation.Behaviors;
using OrientPyx.Presentation.Controls;
using OrientPyx.Presentation.ViewModels.Pages;

namespace OrientPyx.Presentation.Views.Pages;

public partial class FinishReadView : UserControl
{
    private FinishReadViewModel? _vm;

    public FinishReadView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += (_, _) => Unsubscribe();
    }

    // File picking needs the window's StorageProvider, so it lives in the view. The picker only fills
    // the watched path; the VM polls it on its own.
    private async void OnPickFileClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null)
            return;

        var path = await PickCsvAsync();
        if (path is not null)
            _vm.AutoReadFilePath = path;
    }

    private async Task<string?> PickCsvAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null || _vm is null)
            return null;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = _vm.Localization.Get("FinishRead.AutoRead.PickerTitle"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("CSV")
                {
                    Patterns = ["*.csv"],
                    MimeTypes = ["text/csv"]
                }
            ]
        });

        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        Unsubscribe();
        _vm = DataContext as FinishReadViewModel;
        if (_vm is null)
            return;

        _vm.Localization.PropertyChanged += OnLocalizationChanged;
        _vm.Splits.PropertyChanged += OnSplitsChanged;
        _vm.PropertyChanged += OnViewModelChanged;
        BuildBands();
        ArrangeSplit();
        SeedPassageColumns();
    }

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e) => BuildBands();

    // Rebuild the columns when the «Бали» column's visibility flips (the day changed to/from a scoring
    // discipline). Bands bake the visible column set at build time, so they must be rebuilt on a change.
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FinishReadViewModel.ShowScoreColumn))
            BuildBands();
    }

    // Re-arrange the table/panel split when the dock side flips or the panel shows/hides (a hidden panel
    // must give its track back to the table; the size is read from the VM each time). When the ordered
    // layout (re)appears, seed the passage/prescribed star ratio from the saved preference.
    private void OnSplitsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FinishSplitsViewModel.IsDockedRight)
            or nameof(FinishSplitsViewModel.HasData))
            ArrangeSplit();
        if (e.PropertyName is nameof(FinishSplitsViewModel.IsOrdered)
            or nameof(FinishSplitsViewModel.HasData))
            SeedPassageColumns();
    }

    // Applies the saved prescribed-course column width (pixels) to column 2 — the splitter-sized column.
    // It is NOT bound to the VM — the GridSplitter must own it while dragging, so a binding would snap it
    // back — so the saved width is pushed in here once whenever the ordered layout appears.
    private void SeedPassageColumns()
    {
        if (_vm is null || !_vm.Splits.IsOrdered)
            return;

        // Clamp to a sane range so a stale/odd preference can't collapse the column.
        var width = _vm.Splits.PrescribedWidth is > 160 and < 1200 ? _vm.Splits.PrescribedWidth : 320d;
        OrderedArea.ColumnDefinitions[2].Width = new GridLength(width, GridUnitType.Pixel);
    }

    // Lays the table, splitter and splits panel out as either two rows (panel below) or two columns
    // (panel to the right), per Splits.IsDockedRight. The panel track is sized to Splits.PanelSize; the
    // GridSplitter writes the new track length back to the VM after a drag so it persists. Built in
    // code-behind because the Grid's whole row/column shape changes with the dock side.
    private void ArrangeSplit()
    {
        if (_vm is null)
            return;

        var dockRight = _vm.Splits.IsDockedRight;
        // When the panel is hidden its track (and the min) collapse to zero so the table takes the whole area.
        var shown = _vm.Splits.HasData;
        var size = shown ? _vm.Splits.PanelSize : 0;
        var min = shown ? 160d : 0d;

        SplitArea.RowDefinitions.Clear();
        SplitArea.ColumnDefinitions.Clear();

        if (dockRight)
        {
            SplitArea.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star) { MinWidth = 200 });
            SplitArea.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            SplitArea.ColumnDefinitions.Add(new ColumnDefinition(size, GridUnitType.Pixel) { MinWidth = min });

            Place(TablePane, row: 0, col: 0);
            Place(Splitter, row: 0, col: 1);
            Place(SplitsPane, row: 0, col: 2);

            Splitter.Width = 8;
            Splitter.Height = double.NaN;
            Splitter.HorizontalAlignment = HorizontalAlignment.Center;
            Splitter.VerticalAlignment = VerticalAlignment.Stretch;
            Splitter.ResizeDirection = GridResizeDirection.Columns;
        }
        else
        {
            SplitArea.RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star) { MinHeight = 120 });
            SplitArea.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            SplitArea.RowDefinitions.Add(new RowDefinition(size, GridUnitType.Pixel) { MinHeight = min });

            Place(TablePane, row: 0, col: 0);
            Place(Splitter, row: 1, col: 0);
            Place(SplitsPane, row: 2, col: 0);

            Splitter.Height = 8;
            Splitter.Width = double.NaN;
            Splitter.VerticalAlignment = VerticalAlignment.Center;
            Splitter.HorizontalAlignment = HorizontalAlignment.Stretch;
            Splitter.ResizeDirection = GridResizeDirection.Rows;
        }
    }

    private static void Place(Control control, int row, int col)
    {
        Grid.SetRow(control, row);
        Grid.SetColumn(control, col);
    }

    // After a drag, persist the panel's new track length (the relevant axis depends on the dock side).
    private void OnSplitterDragCompleted(object? sender, VectorEventArgs e)
    {
        if (_vm is null)
            return;

        _vm.Splits.PanelSize = _vm.Splits.IsDockedRight ? SplitsPane.Bounds.Width : SplitsPane.Bounds.Height;
    }

    // After resizing the passage|prescribed split, persist the prescribed column's new pixel width so it
    // survives a reselect/reload. Mirrors the outer splitter: read the rendered pane width back from Bounds.
    private void OnPassageSplitterDragCompleted(object? sender, VectorEventArgs e)
    {
        if (_vm is null)
            return;

        var width = PrescribedPane.Bounds.Width;
        if (width > 0)
            _vm.Splits.PrescribedWidth = width;
    }

    // Builds the table's columns. Every column is read-only (no editPath) — the finish log is not
    // edited by the user. Headers are baked into the band model, so a language switch rebuilds.
    private void BuildBands()
    {
        if (_vm is null)
            return;

        var builder = new SheetColumnBuilder(_vm.Localization)
            .Text("FinishRead.Col.Id", nameof(FinishReadRowViewModel.Order), minWidth: 60)
            .Custom("FinishRead.Col.Chip", BuildChipCell, minWidth: 120,
                    sortPath: nameof(FinishReadRowViewModel.ChipNumber))
            .Text("FinishRead.Col.StartTime", nameof(FinishReadRowViewModel.StartTimeText), minWidth: 110)
            .Text("FinishRead.Col.FinishTime", nameof(FinishReadRowViewModel.FinishTimeText), minWidth: 110)
            .Text("FinishRead.Col.ResultTime", nameof(FinishReadRowViewModel.ElapsedText), minWidth: 110);

        // «Бали»: only on point-scoring days (rogaine, or a group overriding to one).
        if (_vm.ShowScoreColumn)
            builder.Text("FinishRead.Col.Score", nameof(FinishReadRowViewModel.ScoreText), minWidth: 70);

        Sheet.Bands = builder
            .Text("FinishRead.Col.Number", nameof(FinishReadRowViewModel.ParticipantNumber), minWidth: 80)
            .Text("FinishRead.Col.FullName", nameof(FinishReadRowViewModel.FullName), minWidth: 220)
            .Text("FinishRead.Col.Group", nameof(FinishReadRowViewModel.GroupName), minWidth: 140)
            // Status: short code (OK/MP/OVT/DNF) with the MP detail as a tooltip.
            .Custom("FinishRead.Col.Status", BuildStatusCell, minWidth: 80,
                    sortPath: nameof(FinishReadRowViewModel.StatusText))
            // Actions: edit + print-splits buttons per row.
            .Custom("FinishRead.Col.Actions", BuildActionsCell, width: 96)
            .Bands;
    }

    // The trailing «Дії» cell: an edit button (opens the edit modal) and a print button (prints the row's
    // split printout). Both bind to the page's commands with the row as the parameter (via the cell's
    // DataContext).
    private Control BuildActionsCell()
    {
        var edit = new Button
        {
            Classes = { "ghost", "small" },
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Content = new PathIcon
            {
                // A pencil glyph.
                Data = Avalonia.Media.Geometry.Parse(
                    "M4,20 h4 L18.5,9.5 a2,2 0 0 0 0,-3 l-1,-1 a2,2 0 0 0 -3,0 L4,16 z"),
                Width = 15,
                Height = 15
            },
            [ToolTip.TipProperty] = _vm!.Localization.Get("FinishRead.Edit.Tooltip"),
        };
        edit.Click += (_, _) =>
        {
            if (edit.DataContext is FinishReadRowViewModel row)
                _vm!.EditRowCommand.Execute(row);
        };

        var print = new Button
        {
            Classes = { "ghost", "small" },
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Content = new PathIcon
            {
                // A printer glyph.
                Data = Avalonia.Media.Geometry.Parse(
                    "M6,9 V3 h12 v6 M6,18 H4 a2,2 0 0 1 -2,-2 v-4 a2,2 0 0 1 2,-2 h16 a2,2 0 0 1 2,2 v4 a2,2 0 0 1 -2,2 h-2 M6,14 h12 v7 h-12 z"),
                Width = 15,
                Height = 15
            },
            [ToolTip.TipProperty] = _vm!.Localization.Get("FinishRead.Print.Tooltip"),
        };
        print.Click += (_, _) =>
        {
            if (print.DataContext is FinishReadRowViewModel row)
                _vm!.PrintRowCommand.Execute(row);
        };

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { edit, print }
        };
    }

    // Read-only chip label, bold-red when the number is not in the rental-chip database — same
    // highlight as the participants table, just on a read-only cell (Text() only wires it on editable
    // columns), so it's built here via ChipHighlight.SetLabelRegistry directly.
    private Control BuildChipCell()
    {
        var block = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(10, 0),
            [!TextBlock.TextProperty] = new Binding(nameof(FinishReadRowViewModel.ChipNumber)),
        };
        ChipHighlight.SetLabelRegistry(block, _vm!.RentalChips);
        return block;
    }

    private static Control BuildStatusCell()
    {
        var block = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(10, 0),
            FontWeight = Avalonia.Media.FontWeight.Medium,
            [!TextBlock.TextProperty] = new Binding(nameof(FinishReadRowViewModel.StatusText)),
            [!ToolTip.TipProperty] = new Binding(nameof(FinishReadRowViewModel.StatusDetail)),
            // Red foreground when the status is not OK (MP / OVT / DNF / DNS / DSQ).
            [!TextBlock.ForegroundProperty] = new Binding(nameof(FinishReadRowViewModel.StatusIsBad))
            {
                Converter = (IValueConverter)Application.Current!.Resources["BoolToDangerBrush"]!,
            },
        };
        return block;
    }

    private void Unsubscribe()
    {
        if (_vm is not null)
        {
            _vm.Localization.PropertyChanged -= OnLocalizationChanged;
            _vm.Splits.PropertyChanged -= OnSplitsChanged;
            _vm.PropertyChanged -= OnViewModelChanged;
        }
        _vm = null;
    }
}
