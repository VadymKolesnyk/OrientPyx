using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Presentation.ViewModels.Pages;

namespace OrientPyx.Presentation.Views.Pages;

/// <summary>
/// Renders a <see cref="SummaryProtocolDocument"/> into a host <see cref="Grid"/> as a print-faithful mock-up
/// of the multi-day summary sheet: stacked group sections, each a bold caption then a boxed <b>two-tier header</b>
/// (leading identity columns + per-day bands over their М / Час [ / Очки] sub-columns + a «Сума» column) and the
/// data rows. Mirrors <see cref="OrientPyx.DataAccess"/>'s DocxSummaryProtocolWriter layout so the preview
/// matches the export. Built in code (the column/band set is dynamic).
///
/// The <b>leading</b> column cells (header + body) are drag sources + drop targets, so the leading columns can be
/// reordered by dragging — exactly like the results-protocol preview. The per-day result bands and the trailing
/// «Сума» are fixed at the end and are NOT draggable. A drop calls back into the host VM
/// (<see cref="SummaryProtocolsViewModel.MoveLeadingColumnByKey"/>).
/// </summary>
public sealed class SummaryPreviewTable
{
    private const string SerifFont = "Times New Roman";
    private const double BodyFontSize = 13.5;
    private const double CaptionFontSize = 16;

    private static readonly FontFamily Serif = new(SerifFont);
    private static readonly IBrush GridBorder = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
    private static readonly IBrush BandFill = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF2));

    // Column highlight while dragging a leading column (matches ProtocolPreviewTable): a translucent accent wash
    // over the hovered column, a fainter wash over the dragged column, and a thick insertion line at the edge.
    private static readonly IBrush DropColumnFill = new SolidColorBrush(Color.FromArgb(0x33, 0x6D, 0x4A, 0xFF));
    private static readonly IBrush DraggedColumnFill = new SolidColorBrush(Color.FromArgb(0x22, 0x6D, 0x4A, 0xFF));
    private static readonly IBrush DropLineBrush = new SolidColorBrush(Color.FromRgb(0x6D, 0x4A, 0xFF));

    // In-process drag payload: the dragged leading column's key (its SummaryColumn name). In-process keeps it
    // local (never serialized to the OS clipboard).
    private static readonly DataFormat<string> ColumnFormat =
        DataFormat.CreateInProcessFormat<string>("orientpyx-summary-column");

    // Preview sheet geometry (kept in sync with LandscapeToPageSize converter + the sheet padding in the View).
    private const double SheetShortSide = 720;
    private const double A4Ratio = 297.0 / 210.0;
    private const double SheetPadding = 26;

    private readonly Grid _host;

    // The host VM, used to apply a leading-column reorder on drop.
    private SummaryProtocolsViewModel? _vm;

    // The currently rendered document, kept so a drag-over can resolve a column key to its leaf index.
    private SummaryProtocolDocument? _document;

    // All cells of each LEADING leaf column (header + body), kept so a drag can tint the whole column. Keyed by
    // leaf column index (only the leading columns are populated; the rest stay empty / non-interactive).
    private readonly Dictionary<int, List<Border>> _leadingCells = [];

    // The drop-insertion line, spanning the whole table. Repositioned during drag-over, hidden otherwise.
    private Rectangle? _dropLine;

    public SummaryPreviewTable(Grid host)
    {
        _host = host;
    }

    /// <summary>Sets the host VM the drag-reorder calls back into.</summary>
    public void SetHost(SummaryProtocolsViewModel? vm) => _vm = vm;

    public void Render(SummaryProtocolDocument? document)
    {
        _host.Children.Clear();
        _host.ColumnDefinitions.Clear();
        _host.RowDefinitions.Clear();
        _leadingCells.Clear();
        _dropLine = null;
        _document = document;

        if (document is null || document.Sections.Count == 0)
            return;

        var widths = ComputeColumnWidths(document);
        var leaf = widths.Length;
        for (var c = 0; c < leaf; c++)
            _host.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(widths[c], GridUnitType.Pixel)));

        var leadCount = document.LeadingColumns.Count;
        for (var c = 0; c < leadCount; c++)
            _leadingCells[c] = [];

        var nameCol = document.NameColumnIndex;
        var row = 0;

        foreach (var section in document.Sections)
        {
            // Group caption (bold), spanning every column.
            _host.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            AddSpanning(section.GroupName, row, leaf, bold: true, size: CaptionFontSize, topMargin: row == 0 ? 0 : 12);
            row++;

            // ── Tier 1 (band row) ───────────────────────────────────────────────────────────────────
            _host.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            // Leading headers span both tiers (rowspan 2) — each a drag source/target for its column.
            for (var c = 0; c < leadCount; c++)
            {
                var header = AddHeader(document.LeadingColumns[c].Caption, c, row, colSpan: 1, rowSpan: 2, shaded: false);
                WireDrag(header, document.LeadingColumns[c].Key);
                _leadingCells[c].Add(header);
            }

            var col = leadCount;
            foreach (var band in document.DayBands)
            {
                AddHeader(band.Caption, col, row, colSpan: band.SubColumns.Count, rowSpan: 1, shaded: true);
                col += band.SubColumns.Count;
            }
            AddHeader(document.TotalColumnHeader, col, row, colSpan: 1, rowSpan: 2, shaded: false);
            row++;

            // ── Tier 2 (sub-column row) ─────────────────────────────────────────────────────────────
            _host.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            col = leadCount;
            foreach (var band in document.DayBands)
            {
                foreach (var sub in band.SubColumns)
                {
                    AddHeader(sub, col, row, colSpan: 1, rowSpan: 1, shaded: false);
                    col++;
                }
            }
            row++;

            // ── Data rows ───────────────────────────────────────────────────────────────────────────
            foreach (var cells in section.Rows)
            {
                _host.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                for (var c = 0; c < leaf; c++)
                {
                    var text = c < cells.Count ? cells[c] : string.Empty;
                    var isName = c == nameCol;
                    var cell = AddBody(text, c, row, centred: !isName, wrap: isName);
                    // Leading body cells are also drag handles, so a column can be grabbed anywhere in it.
                    if (c < leadCount)
                    {
                        WireDrag(cell, document.LeadingColumns[c].Key);
                        _leadingCells[c].Add(cell);
                    }
                }
                row++;
            }
        }

        // The drop-insertion line, spanning every row. Hidden until a drag hovers a leading header.
        _dropLine = new Rectangle
        {
            Width = 3,
            Fill = DropLineBrush,
            HorizontalAlignment = HorizontalAlignment.Left,
            IsHitTestVisible = false,
            IsVisible = false,
            Margin = new Thickness(-1.5, 0, 0, 0)
        };
        Grid.SetColumn(_dropLine, 0);
        Grid.SetRow(_dropLine, 0);
        Grid.SetRowSpan(_dropLine, row);
        _host.Children.Add(_dropLine);
    }

    private void AddSpanning(string text, int row, int columnCount, bool bold, double size, double topMargin)
    {
        var block = new TextBlock
        {
            Text = text,
            FontFamily = Serif,
            Foreground = Brushes.Black,
            FontWeight = bold ? FontWeight.Bold : FontWeight.Normal,
            FontSize = size,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, topMargin, 0, 2)
        };
        Grid.SetColumn(block, 0);
        Grid.SetColumnSpan(block, columnCount);
        Grid.SetRow(block, row);
        _host.Children.Add(block);
    }

    private Border AddHeader(string text, int col, int row, int colSpan, int rowSpan, bool shaded)
    {
        var border = new Border
        {
            BorderBrush = GridBorder,
            BorderThickness = new Thickness(0.8),
            Background = shaded ? BandFill : Brushes.Transparent,
            Padding = new Thickness(3, 2),
            Child = new TextBlock
            {
                Text = text,
                FontFamily = Serif,
                Foreground = Brushes.Black,
                FontWeight = FontWeight.Bold,
                FontSize = BodyFontSize,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            }
        };
        Grid.SetColumn(border, col);
        Grid.SetColumnSpan(border, colSpan);
        Grid.SetRow(border, row);
        Grid.SetRowSpan(border, rowSpan);
        _host.Children.Add(border);
        return border;
    }

    private Border AddBody(string text, int col, int row, bool centred, bool wrap)
    {
        var border = new Border
        {
            BorderBrush = GridBorder,
            BorderThickness = new Thickness(0.8),
            Background = Brushes.Transparent,
            Padding = new Thickness(3, 1.5),
            Child = new TextBlock
            {
                Text = text,
                FontFamily = Serif,
                Foreground = Brushes.Black,
                FontSize = BodyFontSize,
                HorizontalAlignment = centred ? HorizontalAlignment.Center : HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap
            }
        };
        Grid.SetColumn(border, col);
        Grid.SetRow(border, row);
        _host.Children.Add(border);
        return border;
    }

    // ── Leading-column drag-reorder ───────────────────────────────────────────────────────────────────────

    // Makes a leading cell (header or body) a drag source + drop target for its column. The column key travels
    // in the cell's Tag so the same handlers serve header and body cells. The 4-arrow move cursor signals it.
    private void WireDrag(Border cell, string columnKey)
    {
        cell.Tag = columnKey;
        cell.Cursor = new Cursor(StandardCursorType.SizeAll);
        cell.PointerPressed += OnCellPointerPressed;
        cell.AddHandler(DragDrop.DragOverEvent, OnCellDragOver);
        cell.AddHandler(DragDrop.DropEvent, OnCellDrop);
        DragDrop.SetAllowDrop(cell, true);
    }

    private async void OnCellPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { Tag: string key } cell)
            return;
        if (!e.GetCurrentPoint(cell).Properties.IsLeftButtonPressed)
            return;

        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(ColumnFormat, key));

        TintDraggedColumn(key);
        try
        {
            await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move);
        }
        finally
        {
            ClearHighlight();
        }
    }

    private static bool TryGetDragged(DragEventArgs e, out string key)
    {
        if (e.DataTransfer.TryGetValue(ColumnFormat) is { } k)
        {
            key = k;
            return true;
        }
        key = string.Empty;
        return false;
    }

    private void OnCellDragOver(object? sender, DragEventArgs e)
    {
        if (sender is not Border { Tag: string targetKey } cell || !TryGetDragged(e, out var draggedKey))
        {
            e.DragEffects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.DragEffects = DragDropEffects.Move;
        e.Handled = true;

        var targetIndex = LeafIndexOf(targetKey);
        if (targetIndex < 0)
            return;

        var insertAfter = e.GetPosition(cell).X > cell.Bounds.Width / 2;
        Highlight(draggedKey, targetIndex, insertAfter);
    }

    private void OnCellDrop(object? sender, DragEventArgs e)
    {
        ClearHighlight();

        if (sender is not Border { Tag: string targetKey } cell || !TryGetDragged(e, out var draggedKey))
            return;

        var insertAfter = e.GetPosition(cell).X > cell.Bounds.Width / 2;
        _vm?.MoveLeadingColumnByKey(draggedKey, targetKey, insertAfter);
        e.Handled = true;
    }

    // The leaf-column index of the leading column with the given key, or -1.
    private int LeafIndexOf(string key)
    {
        if (_document is null)
            return -1;
        for (var c = 0; c < _document.LeadingColumns.Count; c++)
            if (_document.LeadingColumns[c].Key == key)
                return c;
        return -1;
    }

    // ── Drag highlight ───────────────────────────────────────────────────────────────────────────────────

    private void TintDraggedColumn(string draggedKey)
    {
        ClearHighlight();
        var dragged = LeafIndexOf(draggedKey);
        if (dragged >= 0)
            FillColumn(dragged, DraggedColumnFill);
    }

    private void Highlight(string draggedKey, int targetIndex, bool insertAfter)
    {
        ClearHighlight();

        var dragged = LeafIndexOf(draggedKey);
        if (dragged >= 0)
            FillColumn(dragged, DraggedColumnFill);
        if (targetIndex >= 0)
            FillColumn(targetIndex, DropColumnFill);

        if (_dropLine is { } line)
        {
            Grid.SetColumn(line, targetIndex);
            line.HorizontalAlignment = insertAfter ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            line.Margin = insertAfter ? new Thickness(0, 0, -1.5, 0) : new Thickness(-1.5, 0, 0, 0);
            line.IsVisible = true;
        }
    }

    private void FillColumn(int leafIndex, IBrush brush)
    {
        if (_leadingCells.TryGetValue(leafIndex, out var cells))
            foreach (var cell in cells)
                cell.Background = brush;
    }

    private void ClearHighlight()
    {
        foreach (var cells in _leadingCells.Values)
            foreach (var cell in cells)
                cell.Background = Brushes.Transparent;
        if (_dropLine is { } line)
            line.IsVisible = false;
    }

    // Leaf-column pixel widths. Mirrors DocxSummaryProtocolWriter.ComputeColumnWidths (which mirrors the results
    // protocol's tiered, priority-based system): each column is sized to content (longest value for short-code
    // columns; a typical value for wrapping free-text columns), then laid out across the printable width in
    // three tiers — guarantee the data floors (squeezing the shrinkable columns by shrink priority on overflow),
    // raise toward the header-word floors, then hand the rest out by natural want so the name column grows. So
    // the preview lines up with the .docx and the per-day protocol.
    private double[] ComputeColumnWidths(SummaryProtocolDocument doc)
    {
        var count = doc.LeafColumnCount;
        const double charPx = BodyFontSize * 0.52;
        const double padPx = 8;
        const double minColPx = 22;

        var captions = LeafCaptions(doc);
        var headerWordChars = new int[count];
        for (var c = 0; c < count; c++)
            headerWordChars[c] = HeaderWidthChars(captions[c]);

        var maxCell = new int[count];
        var sumCell = new long[count];
        var filledCount = new int[count];
        foreach (var cells in doc.Sections.SelectMany(s => s.Rows))
            for (var c = 0; c < count && c < cells.Count; c++)
            {
                var len = cells[c]?.Length ?? 0;
                maxCell[c] = Math.Max(maxCell[c], len);
                if (len > 0)
                {
                    sumCell[c] += len;
                    filledCount[c]++;
                }
            }

        var dataChars = new int[count];
        for (var c = 0; c < count; c++)
        {
            var wraps = c < doc.ColumnBodyWrap.Count && doc.ColumnBodyWrap[c];
            if (wraps && filledCount[c] > 0)
            {
                var mean = (double)sumCell[c] / filledCount[c];
                var target = Math.Clamp((int)Math.Ceiling(mean * WrapMeanSlack), WrapColumnMinChars, WrapColumnMaxChars);
                dataChars[c] = Math.Min(maxCell[c], target);
            }
            else
            {
                dataChars[c] = maxCell[c];
            }
        }

        var dataFloor = new double[count];
        var preferredFloor = new double[count];
        var natural = new double[count];
        for (var c = 0; c < count; c++)
        {
            dataFloor[c] = Math.Max(minColPx, dataChars[c] * charPx + padPx);
            preferredFloor[c] = Math.Max(dataFloor[c], headerWordChars[c] * charPx + padPx);
            natural[c] = Math.Max(preferredFloor[c], dataChars[c] * charPx + padPx);
        }

        var priority = new int[count];
        for (var c = 0; c < count; c++)
            priority[c] = c < doc.ColumnShrinkPriority.Count ? doc.ColumnShrinkPriority[c] : 1;

        var shrinkFloor = new double[count];
        for (var c = 0; c < count; c++)
            shrinkFloor[c] = ShrinkFloor(priority[c], dataFloor[c], preferredFloor[c], minColPx);

        var landscape = doc.Orientation == ProtocolOrientation.Landscape;
        var sheetWidth = landscape ? SheetShortSide * A4Ratio : SheetShortSide;
        var printable = sheetWidth - 2 * SheetPadding;

        var widths = DistributeWidths(dataFloor, preferredFloor, natural, shrinkFloor, priority, printable);

        var slackCol = doc.NameColumnIndex >= 0 ? doc.NameColumnIndex : 0;
        widths[slackCol] += printable - widths.Sum();
        return widths;
    }

    private static string[] LeafCaptions(SummaryProtocolDocument doc)
    {
        var captions = new string[doc.LeafColumnCount];
        var leadCount = doc.LeadingColumns.Count;
        for (var c = 0; c < leadCount; c++)
            captions[c] = doc.LeadingColumns[c].Caption;
        var col = leadCount;
        foreach (var band in doc.DayBands)
            foreach (var sub in band.SubColumns)
                captions[col++] = sub;
        captions[col] = doc.TotalColumnHeader;
        return captions;
    }

    private static double ShrinkFloor(int priority, double dataFloor, double preferredFloor, double absoluteMin) =>
        priority switch
        {
            <= 1 => preferredFloor,
            2 => Math.Max(absoluteMin, dataFloor * 0.85),
            3 => Math.Max(absoluteMin, dataFloor * 0.70),
            _ => Math.Max(absoluteMin, dataFloor * 0.55),
        };

    private static double[] DistributeWidths(double[] dataFloor, double[] preferredFloor, double[] natural,
        double[] shrinkFloor, int[] priority, double printable)
    {
        var count = dataFloor.Length;
        var widths = new double[count];

        var dataTotal = dataFloor.Sum();

        if (dataTotal >= printable)
        {
            for (var c = 0; c < count; c++)
                widths[c] = dataFloor[c];
            ShrinkByDeficit(widths, shrinkFloor, priority, dataTotal - printable);
            return widths;
        }

        for (var c = 0; c < count; c++)
            widths[c] = dataFloor[c];
        var toPreferred = printable - dataTotal;
        var headerWant = 0.0;
        var hWant = new double[count];
        for (var c = 0; c < count; c++)
        {
            hWant[c] = preferredFloor[c] - dataFloor[c];
            headerWant += hWant[c];
        }
        if (headerWant >= toPreferred)
        {
            for (var c = 0; c < count; c++)
                widths[c] += headerWant > 0 ? hWant[c] * toPreferred / headerWant : 0;
            return widths;
        }

        for (var c = 0; c < count; c++)
            widths[c] = preferredFloor[c];
        var slack = printable - widths.Sum();
        var wantTotal = 0.0;
        var want = new double[count];
        for (var c = 0; c < count; c++)
        {
            want[c] = Math.Max(0, natural[c] - preferredFloor[c]);
            wantTotal += want[c];
        }
        for (var c = 0; c < count; c++)
            widths[c] += wantTotal > 0 ? want[c] * slack / wantTotal : slack / count;
        return widths;
    }

    private static double ShrinkWeight(int priority) => priority switch { 4 => 3, 3 => 2, 2 => 1, _ => 0 };

    private static void ShrinkByDeficit(double[] widths, double[] shrinkFloor, int[] priority, double deficit)
    {
        if (deficit <= 0)
            return;

        var count = widths.Length;
        var weight = new double[count];
        var open = new bool[count];
        for (var c = 0; c < count; c++)
        {
            weight[c] = ShrinkWeight(priority[c]);
            open[c] = weight[c] > 0 && widths[c] > shrinkFloor[c];
        }

        for (var pass = 0; pass < count && deficit > 1e-6; pass++)
        {
            var weightTotal = 0.0;
            for (var c = 0; c < count; c++)
                if (open[c])
                    weightTotal += weight[c];
            if (weightTotal <= 0)
                break;

            var clampedAny = false;
            var remaining = deficit;
            for (var c = 0; c < count; c++)
            {
                if (!open[c])
                    continue;
                var share = deficit * weight[c] / weightTotal;
                var room = widths[c] - shrinkFloor[c];
                if (share >= room)
                {
                    share = room;
                    open[c] = false;
                    clampedAny = true;
                }
                widths[c] -= share;
                remaining -= share;
            }
            deficit = remaining;
            if (!clampedAny)
                break;
        }

        if (deficit > 1e-6)
        {
            var total = widths.Sum();
            var target = total - deficit;
            if (total > 0 && target > 0)
                for (var c = 0; c < count; c++)
                    widths[c] = widths[c] * target / total;
        }
    }

    private const int HeaderWordCap = 8;
    private const double WrapMeanSlack = 1.35;
    private const int WrapColumnMinChars = 6;
    private const int WrapColumnMaxChars = 20;
    private const int HeaderSafetyChars = 1;

    private static int HeaderWidthChars(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
            return 0;
        var longestWord = header.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Length).DefaultIfEmpty(0).Max();
        return Math.Min(longestWord, HeaderWordCap) + HeaderSafetyChars;
    }
}
