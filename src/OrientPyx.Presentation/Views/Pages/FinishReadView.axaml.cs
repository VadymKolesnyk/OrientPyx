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
using Avalonia.Media;
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
            // Tint the whole chip cell amber when it's a rental chip due for collection (the holder's last
            // day with it), with a tooltip explaining why.
            .CellTint(nameof(FinishReadRowViewModel.CollectRentalChip), CollectRentalBrush,
                      nameof(FinishReadRowViewModel.CollectRentalChipTooltip))
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
            // Place: 1-based rank within the group (blank when none), with a gold/silver/bronze badge on the podium.
            .Custom("FinishRead.Col.Place", BuildPlaceCell, minWidth: 70,
                    sortPath: nameof(FinishReadRowViewModel.PlaceText))
            // Gap: loss to the group leader («Відставання»), blank for the leader / unplaced rows.
            .Text("FinishRead.Col.Gap", nameof(FinishReadRowViewModel.GapText), minWidth: 100)
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

    // Amber tint painted over a whole chip cell whose rental chip is due for collection (the holder's last
    // day with it). Light enough that the theme's chip text stays legible over it in both themes.
    private static readonly ISolidColorBrush CollectRentalBrush = new SolidColorBrush(Color.FromRgb(0xF6, 0xC3, 0x43));

    // Read-only chip label, bold-red when the number is NOT a rental chip (same highlight as the
    // participants table; Text() only wires it on editable columns, so it's set here directly). The amber
    // "collect this rental chip" tint + tooltip are painted on the whole cell by the column's
    // CellBackgroundPath (see BuildBands), not here — so the tint covers the cell, not just the text.
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

    // Gold/silver/bronze for places 1/2/3; nothing for any other place (and for none). Slightly muted
    // tones so the dark text stays legible on the badge in both light and dark themes.
    private static readonly ISolidColorBrush GoldBrush = new SolidColorBrush(Color.FromRgb(0xF2, 0xC4, 0x3D));
    private static readonly ISolidColorBrush SilverBrush = new SolidColorBrush(Color.FromRgb(0xC7, 0xCD, 0xD4));
    private static readonly ISolidColorBrush BronzeBrush = new SolidColorBrush(Color.FromRgb(0xD8, 0x9A, 0x5B));

    // The place cell: the rank number, wrapped in a rounded medal badge coloured for a podium place
    // (1/2/3 → gold/silver/bronze) and left as a plain number otherwise. Recomputed on every rebuild,
    // which the VM triggers after each read-out, so a changed place re-tints automatically.
    private static Control BuildPlaceCell()
    {
        // Podium places (1/2/3) sit on a coloured badge, so force dark text for contrast; every other place
        // keeps the theme's default foreground. Binding a null Foreground renders the text invisible rather
        // than falling back, so non-podium rows must use an explicit brush — not null.
        var defaultForeground = Application.Current!.Resources.TryGetResource(
            "TextPrimary", null, out var fg) && fg is IBrush b ? b : Brushes.Black;
        var text = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
            [!TextBlock.TextProperty] = new Binding(nameof(FinishReadRowViewModel.PlaceText)),
            [!TextBlock.ForegroundProperty] = new Binding(nameof(FinishReadRowViewModel.PlaceMedal))
            {
                Converter = new FuncValueConverter<int, IBrush?>(m => m is 1 or 2 or 3
                    ? Brushes.Black
                    : defaultForeground),
            },
        };

        var badge = new Border
        {
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(9, 1),
            MinWidth = 26,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = text,
            [!Border.BackgroundProperty] = new Binding(nameof(FinishReadRowViewModel.PlaceMedal))
            {
                Converter = new FuncValueConverter<int, IBrush?>(m => m switch
                {
                    1 => GoldBrush,
                    2 => SilverBrush,
                    3 => BronzeBrush,
                    _ => null,
                }),
            },
        };

        return badge;
    }

    // Status-cell brushes, resolved once from the theme: green for a manual "cleared to OK" ruling, red for
    // any bad status (computed or manual). Fall back to fixed hexes if a resource is somehow missing.
    private static readonly IBrush SuccessBrush =
        Application.Current!.Resources.TryGetResource("SuccessBrush", null, out var s) && s is IBrush sb
            ? sb : new SolidColorBrush(Color.Parse("#059669"));
    private static readonly IBrush DangerBrush =
        Application.Current!.Resources.TryGetResource("DangerBrush", null, out var d) && d is IBrush db
            ? db : new SolidColorBrush(Color.Parse("#DC2626"));

    private static Control BuildStatusCell()
    {
        var block = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(10, 0),
            [!ToolTip.TipProperty] = new Binding(nameof(FinishReadRowViewModel.StatusDetail)),
            [!TextBlock.TextProperty] = new Binding(nameof(FinishReadRowViewModel.StatusText)),
            // Bold when the status is a manual override (judge's ruling), so it stands out from a computed
            // one; a computed status keeps the medium weight.
            [!TextBlock.FontWeightProperty] = new Binding(nameof(FinishReadRowViewModel.StatusIsManual))
            {
                Converter = new FuncValueConverter<bool, FontWeight>(
                    manual => manual ? FontWeight.Bold : FontWeight.Medium),
            },
        };

        // Foreground: a manual override to OK is green (a "cleared to OK" ruling); any bad status is red;
        // everything else inherits the default. Combines both flags so manual-OK beats the bad-tint.
        block[!TextBlock.ForegroundProperty] = new MultiBinding
        {
            Bindings =
            {
                new Binding(nameof(FinishReadRowViewModel.StatusIsManualOk)),
                new Binding(nameof(FinishReadRowViewModel.StatusIsBad)),
            },
            Converter = new FuncMultiValueConverter<bool, IBrush?>(flags =>
            {
                var list = flags.ToArray();
                var manualOk = list.Length > 0 && list[0];
                var bad = list.Length > 1 && list[1];
                return manualOk ? SuccessBrush : bad ? DangerBrush : null;
            }),
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
