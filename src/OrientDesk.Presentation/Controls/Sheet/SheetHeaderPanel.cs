using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using OrientDesk.Localization;
using OrientDesk.Presentation.ViewModels.Pages;

namespace OrientDesk.Presentation.Controls;

/// <summary>
/// The roster's two-tier (banded) header. Top tier carries identity headers (spanning both rows)
/// and field-block band labels (spanning their day sub-columns, with a collapse chevron); bottom
/// tier carries the "День N" sub-headers. Each leaf has a right-edge resize grip writing the shared
/// <see cref="SheetColumn.Width"/>. Clicking a header sorts by that column. The whole band is a
/// drag unit for reordering (a grouped field-block never splits) with a drop indicator line.
/// </summary>
internal sealed class SheetHeaderPanel : Grid
{
    private readonly ILocalizationService _loc;

    // Per-band top-tier cell, so the drop indicator can be positioned at a band boundary.
    private readonly List<Control> _bandHeaders = [];
    private IReadOnlyList<SheetBand> _bands = [];
    private readonly Rectangle _dropLine;
    // Translucent overlay over the band currently being dragged, so the user sees what they're moving.
    private readonly Rectangle _dragHighlight;

    // Drag state.
    private bool _dragArmed;
    private bool _dragging;
    private Point _pressOrigin;
    private int _dragBandIndex = -1;

    public SheetHeaderPanel(ILocalizationService localization)
    {
        _loc = localization;
        RowDefinitions = new RowDefinitions("20,Auto");
        HorizontalAlignment = HorizontalAlignment.Left;
        ClipToBounds = true;

        _dropLine = new Rectangle
        {
            Width = 3,
            Fill = Brushes.DodgerBlue,
            IsHitTestVisible = false,
            IsVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(0)
        };

        _dragHighlight = new Rectangle
        {
            Fill = new SolidColorBrush(Colors.DodgerBlue, 0.14),
            IsHitTestVisible = false,
            IsVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Stretch
        };
    }

    /// <summary>The block collapse/expand command, bound live so it can be set after construction.</summary>
    public System.Windows.Input.ICommand? ToggleBlock { get; set; }

    /// <summary>Invoked when a header is clicked to sort by the given column.</summary>
    public Action<SheetColumn>? SortBy { get; set; }

    /// <summary>Invoked to move a band from one top-level index to another (reorder).</summary>
    public Action<int, int>? MoveBand { get; set; }

    /// <summary>Invoked from a header's context menu to hide that leaf column.</summary>
    public Action<SheetColumn>? HideColumn { get; set; }

    /// <summary>Invoked when a column resize (grip drag) completes, so the new width can be persisted.</summary>
    public Action? ColumnResized { get; set; }

    /// <summary>Invoked from a header's context menu to open the column's filter editor.</summary>
    public Action<SheetColumn>? FilterColumn { get; set; }

    /// <summary>Invoked from a header's context menu to remove the column's filter.</summary>
    public Action<SheetColumn>? RemoveFilter { get; set; }

    /// <summary>Tells the menu whether a column currently has an active filter (to show "remove").</summary>
    public Func<SheetColumn, bool>? HasFilter { get; set; }

    // The header cell control built for each leaf column, so the table can anchor a filter popup at the
    // right header when it is opened from the column's menu. Rebuilt on every Rebuild().
    private readonly Dictionary<string, Control> _headerCells = new();

    /// <summary>The header cell control for a leaf column (by key), or null if not currently shown.</summary>
    public Control? HeaderCellFor(SheetColumn column)
        => _headerCells.TryGetValue(column.Key, out var c) ? c : null;

    /// <summary>Width of the resize-grip hit zone at each column's right edge (px).</summary>
    private const double GripWidth = 8;

    /// <summary>Right margin for a header's sort button, leaving just enough to clear the grip line.</summary>
    private const double SortRightMargin = 3;

    /// <summary>The column currently sorted, and the direction, so the arrow indicator can render.</summary>
    public SheetColumn? SortColumn { get; set; }
    public bool SortDescending { get; set; }

    /// <summary>Rebuilds the header grid for the given bands.</summary>
    public void Rebuild(IReadOnlyList<SheetBand> bands)
    {
        _bands = bands;
        _bandHeaders.Clear();
        _headerCells.Clear();
        Children.Clear();
        ColumnDefinitions.Clear();

        // One grid column per leaf, its width two-way bound to the shared SheetColumn.Width.
        var leaves = new List<SheetColumn>();
        foreach (var band in bands)
            leaves.AddRange(band.Columns);

        for (var i = 0; i < leaves.Count; i++)
        {
            var def = new ColumnDefinition { MinWidth = leaves[i].MinWidth };
            def[!ColumnDefinition.WidthProperty] = new Binding(nameof(SheetColumn.Width))
            {
                Source = leaves[i],
                Mode = BindingMode.TwoWay,
                Converter = PixelToGridLength.Instance
            };
            ColumnDefinitions.Add(def);
        }

        var col = 0;
        for (var b = 0; b < bands.Count; b++)
        {
            var band = bands[b];
            var span = band.Columns.Count;

            if (band.Kind == SheetBand.BandKind.Identity)
            {
                var header = BuildHeaderText(band.Header, band.Columns[0], band, b);
                SetColumn(header, col);
                SetRow(header, 0);
                SetRowSpan(header, 2);
                Children.Add(header);
                _bandHeaders.Add(header);
                AddResizeGrip(band.Columns[0], col, rowSpan: 2);
            }
            else
            {
                // Band label across the day sub-columns (top tier) + collapse toggle.
                var banner = BuildBandBanner(band, b);
                SetColumn(banner, col);
                SetColumnSpan(banner, span);
                SetRow(banner, 0);
                Children.Add(banner);
                _bandHeaders.Add(banner);

                if (band.IsCollapsed)
                {
                    // Collapsed ⇒ one merged column: the banner is the only header (it carries the
                    // single "general" sort button) and spans both tiers. No per-day sub-header below.
                    SetRowSpan(banner, 2);
                    AddResizeGrip(band.Columns[0], col, rowSpan: 2);
                }
                else
                {
                    // Expanded ⇒ a "День N" sub-header per leaf, each with its OWN sort button and
                    // resize grip (the banner shows no sort button in this state).
                    for (var k = 0; k < span; k++)
                    {
                        var sub = BuildSubHeaderText(band.Columns[k], b);
                        SetColumn(sub, col + k);
                        SetRow(sub, 1);
                        Children.Add(sub);
                        // Inner grips sit on the bottom tier only (the banner spans the top tier across
                        // the whole band). The LAST grip is the band's right edge, so it spans both tiers
                        // — otherwise its separator line stops at the sub-header and the band's right
                        // border looks half-height under the banner.
                        var lastInBand = k == span - 1;
                        AddResizeGrip(band.Columns[k], col + k,
                            rowSpan: lastInBand ? 2 : 1,
                            row: lastInBand ? 0 : 1);
                    }
                }
            }

            col += span;
        }

        // Drag highlight + drop indicator overlay all columns; positioned by margin during a drag.
        // Highlight first so the drop line draws on top of it.
        SetColumn(_dragHighlight, 0);
        SetColumnSpan(_dragHighlight, Math.Max(1, leaves.Count));
        SetRow(_dragHighlight, 0);
        SetRowSpan(_dragHighlight, 2);
        Children.Add(_dragHighlight);

        SetColumn(_dropLine, 0);
        SetColumnSpan(_dropLine, Math.Max(1, leaves.Count));
        SetRow(_dropLine, 0);
        SetRowSpan(_dropLine, 2);
        Children.Add(_dropLine);
    }

    // ── Header cells ────────────────────────────────────────────────────────────────────────────
    // An identity header: label (stretch) + a small sort button on the right; the whole cell is a
    // band-drag handle (sort button excluded so its click sorts rather than drags).
    private Border BuildHeaderText(string text, SheetColumn column, SheetBand band, int bandIndex)
    {
        var inner = BuildLabelWithSort(text, column);
        // Left-only padding: the sort button must reach the column's right edge (matching the banner);
        // a symmetric right pad would leave a gap between it and the resize grip. Transparent so the
        // whole cell (incl. empty space) is hit-testable for the drag.
        var border = new Border { Background = Brushes.Transparent, Padding = new Thickness(10, 0, 0, 0), Child = inner };
        WireHeaderInteractions(border, bandIndex);
        AttachHideMenu(border, column);
        return border;
    }

    // A "День N" sub-header: label + per-day sort button. The whole sub-header is also a band-drag
    // handle (the band drags as a whole from any of its sub-columns), with the sort button excluded.
    private Border BuildSubHeaderText(SheetColumn column, int bandIndex)
    {
        var inner = BuildLabelWithSort(column.Header, column);
        // Left-only padding so the per-day sort button reaches the sub-column's right edge (see
        // BuildHeaderText). Transparent so the whole sub-cell is hit-testable for the band drag.
        var border = new Border { Background = Brushes.Transparent, Padding = new Thickness(10, 0, 0, 0), Child = inner };
        WireBandDrag(border, bandIndex);
        AttachHideMenu(border, column);
        return border;
    }

    // Label + right-pinned sort icon button for a sortable column. The sort button is sized large and
    // hugs the right edge (clear of the resize grip); the label gets the remaining space.
    private Grid BuildLabelWithSort(string text, SheetColumn column)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            // Stretch to the full cell width so the Auto sort column lands on the right edge
            // (matching the collapsed-band banner); a content-sized grid would leave it mid-cell.
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = Brushes.Transparent
        };
        var label = new TextBlock
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        SetColumn(label, 0);
        grid.Children.Add(label);

        if (!string.IsNullOrEmpty(column.SortPath))
        {
            var btn = BuildSortButton(column);
            btn.HorizontalAlignment = HorizontalAlignment.Right;
            btn.Margin = new Thickness(2, 0, SortRightMargin, 0); // pin hard right, just clearing the grip line
            SetColumn(btn, 1);
            grid.Children.Add(btn);
        }
        return grid;
    }

    // Small ghost icon button that sorts by the column. Shows a neutral up/down glyph when inactive,
    // a directional arrow when this column is the active sort.
    private Button BuildSortButton(SheetColumn column)
    {
        var active = SortColumn == column;
        var icon = new PathIcon
        {
            Width = 13,
            Height = 13,
            Foreground = active ? Brushes.DodgerBlue : Brushes.Gray,
            Data = Geometry.Parse(active
                ? (SortDescending ? "M2,4 L8,4 L5,9 Z" : "M5,1 L8,6 L2,6 Z")
                : "M5,0 L8,4 L2,4 Z M5,10 L2,6 L8,6 Z") // neutral: up+down chevrons
        };
        var btn = new Button
        {
            Classes = { "ghost", "rosterSort" },
            Padding = new Thickness(4, 2),
            MinWidth = 0,
            MinHeight = 0,
            VerticalAlignment = VerticalAlignment.Center,
            Content = icon,
            [ToolTip.TipProperty] = _loc.Get("Common.Sort")
        };
        btn.Click += (_, _) => SortBy?.Invoke(column);
        return btn;
    }

    private Border BuildBandBanner(SheetBand band, int bandIndex)
    {
        var chevron = new PathIcon
        {
            Width = 10,
            Height = 10,
            Data = Geometry.Parse(band.IsCollapsed ? "M6,4 L10,8 L6,12 Z" : "M4,6 L8,10 L12,6 Z")
        };
        // The band label reads as plain clickable text (not a button): click toggles collapse, and
        // a press-and-drag anywhere on the banner reorders the band. The whole label area is a
        // transparent, hit-testable click target. Laid out as a Grid ("*,Auto") so the text trims with
        // an ellipsis when the band is narrower than its label rather than bleeding into the next band.
        var label = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = Brushes.Transparent,
            Margin = new Thickness(10, 0),
            Cursor = new Cursor(StandardCursorType.Hand),
            [ToolTip.TipProperty] = _loc.Get(band.IsCollapsed ? "Participants.Roster.Expand" : "Participants.Roster.Collapse")
        };
        var bannerText = new TextBlock
        {
            Text = band.Header,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 6, 0),
            Foreground = (IBrush?)this.FindResource("TextSecondary"),
            FontWeight = FontWeight.Medium
        };
        SetColumn(bannerText, 0);
        SetColumn(chevron, 1);
        label.Children.Add(bannerText);
        label.Children.Add(chevron);
        // Toggle on a click that didn't turn into a drag (tracked via _dragging at release).
        label.PointerReleased += (_, e) =>
        {
            if (!_dragging && e.InitialPressMouseButton == MouseButton.Left && !IsOnSortButton(e.Source))
                ToggleBlock?.Execute(band.Block);
        };

        // The banner is a Grid filling the whole band: a transparent backdrop (so the EMPTY area is
        // hit-testable and drags), the label on the left, and — when collapsed — the single merged
        // column's sort button pinned to the right edge.
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Background = Brushes.Transparent
        };
        SetColumn(label, 0);
        grid.Children.Add(label);

        if (band.IsCollapsed && band.Columns.Count > 0 && !string.IsNullOrEmpty(band.Columns[0].SortPath))
        {
            var sort = BuildSortButton(band.Columns[0]);
            sort.HorizontalAlignment = HorizontalAlignment.Right;
            sort.Margin = new Thickness(2, 0, SortRightMargin, 0); // pin hard right, just clearing the grip line
            SetColumn(sort, 1);
            grid.Children.Add(sort);
        }

        // Wrap in a Border so a collapsed band still shows a full-height right separator (the
        // sub-header row that normally carries it is absent when collapsed).
        var border = new Border
        {
            Background = Brushes.Transparent,
            Child = grid
        };

        // A press anywhere on the banner can start a band drag (the label click/sort clicks still
        // work — a press without movement does not become a drag).
        WireBandDrag(border, bandIndex);
        // A collapsed block is one merged column, so the banner itself can be hidden via its menu.
        if (band.IsCollapsed && band.Columns.Count > 0)
            AttachHideMenu(border, band.Columns[0]);
        return border;
    }

    // A right-click context menu on a header cell: Filter… / Remove filter (when set) / Hide column.
    // Bound to the leaf column the header sits over; the items call back into the table. Uses a Flyout
    // (not a ContextMenu) so its content can be wrapped in a UiScale LayoutTransformControl — a
    // ContextMenu's own popup lives outside the window's root transform and would render unscaled (see
    // PopupScaling). Also records the header cell so the table can anchor the filter popup here.
    private void AttachHideMenu(Control header, SheetColumn column)
    {
        _headerCells[column.Key] = header;
        header.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Right)
                return;
            e.Handled = true;
            ShowHeaderMenu(header, column);
        };
    }

    private void ShowHeaderMenu(Control header, SheetColumn column)
    {
        var menu = new StackPanel { Spacing = 2, Margin = new Thickness(4) };
        var flyout = new Flyout
        {
            Placement = PlacementMode.BottomEdgeAlignedLeft,
            Content = new LayoutTransformControl
            {
                LayoutTransform = SheetColumnsButton.BuildUiScaleTransform(),
                Child = menu
            }
        };

        Button MenuItem(string textKey, Action onClick)
        {
            var b = new Button
            {
                Classes = { "ghost" },
                Content = _loc.Get(textKey),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(10, 6)
            };
            b.Click += (_, _) => { flyout.Hide(); onClick(); };
            return b;
        }

        if (column.Filterable && FilterColumn is not null)
        {
            var hasFilter = HasFilter?.Invoke(column) == true;
            menu.Children.Add(MenuItem(hasFilter ? "Sheet.Filter.Edit" : "Sheet.Filter.Add",
                () => FilterColumn.Invoke(column)));
            if (hasFilter)
                menu.Children.Add(MenuItem("Sheet.Filter.Remove", () => RemoveFilter?.Invoke(column)));
        }

        menu.Children.Add(MenuItem("Sheet.Columns.Hide", () => HideColumn?.Invoke(column)));
        flyout.ShowAt(header);
    }

    // ── Sort + drag wiring on an identity header cell ───────────────────────────────────────────
    private void WireHeaderInteractions(Control header, int bandIndex)
    {
        header.PointerPressed += (_, e) =>
        {
            // A press starting on the sort button (or any interactive child) must NOT arm a band drag,
            // or the parent would steal the pointer and swallow the button click.
            if (!e.GetCurrentPoint(header).Properties.IsLeftButtonPressed || IsOnSortButton(e.Source))
                return;
            _pressOrigin = e.GetPosition(this);
            ArmDrag(bandIndex);
        };
        header.PointerMoved += (_, e) =>
        {
            UpdateDrag(e);
            if (_dragging && e.Pointer.Captured is null)
                e.Pointer.Capture(header);
        };
        header.PointerReleased += (_, e) =>
        {
            if (_dragging)
                FinishDrag(e);
        };
        header.PointerCaptureLost += (_, _) => CancelDrag();
    }

    private void WireBandDrag(Control banner, int bandIndex)
    {
        banner.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(banner).Properties.IsLeftButtonPressed && !IsOnSortButton(e.Source))
            {
                _pressOrigin = e.GetPosition(this);
                ArmDrag(bandIndex);
                // Don't capture yet: let the toggle button handle a plain click (collapse toggle).
                // Capture only once an actual drag starts (in UpdateDrag) so the chevron still works.
            }
        };
        banner.PointerMoved += (_, e) =>
        {
            UpdateDrag(e);
            if (_dragging && e.Pointer.Captured is null)
                e.Pointer.Capture(banner);
        };
        banner.PointerReleased += (_, e) =>
        {
            if (_dragging)
                FinishDrag(e);
        };
        banner.PointerCaptureLost += (_, _) => CancelDrag();
    }

    // True when a pointer event originated on (or inside) a sort button, so a band drag is skipped.
    private static bool IsOnSortButton(object? source)
    {
        var v = source as Avalonia.Visual;
        while (v is not null)
        {
            if (v is Button b && b.Classes.Contains("rosterSort"))
                return true;
            v = v.GetVisualParent();
        }
        return false;
    }

    // ── Drag-reorder of whole bands ─────────────────────────────────────────────────────────────
    private void ArmDrag(int bandIndex)
    {
        _dragArmed = true;
        _dragBandIndex = bandIndex;
    }

    private void UpdateDrag(PointerEventArgs e)
    {
        if (!_dragArmed)
            return;

        var pos = e.GetPosition(this);
        if (!_dragging)
        {
            if (Math.Abs(pos.X - _pressOrigin.X) < 6)
                return; // not yet a drag
            _dragging = true;
            ShowDragHighlight(_dragBandIndex);
        }

        // Show the drop line at the nearest band boundary.
        var target = BandBoundaryAt(pos.X);
        ShowDropLine(target);
    }

    private void FinishDrag(PointerEventArgs e)
    {
        var pos = e.GetPosition(this);
        var insertBefore = BandBoundaryAt(pos.X);
        HideDropLine();

        var from = _dragBandIndex;
        // Convert an insert-before-boundary index into a destination band index.
        var to = insertBefore;
        if (to > from)
            to--; // removing the source first shifts everything after it left by one

        if (from >= 0 && to >= 0 && to != from)
            MoveBand?.Invoke(from, to);

        CancelDrag();
    }

    private void CancelDrag()
    {
        _dragArmed = false;
        _dragging = false;
        _dragBandIndex = -1;
        HideDropLine();
        _dragHighlight.IsVisible = false;
    }

    // Tint the whole dragged band (all its day sub-columns) so the user sees what's moving.
    private void ShowDragHighlight(int bandIndex)
    {
        if (bandIndex < 0 || bandIndex >= _bands.Count)
            return;
        var left = BoundaryX(bandIndex);
        double width = 0;
        foreach (var c in _bands[bandIndex].Columns)
            width += c.Width;
        _dragHighlight.Width = Math.Max(0, width);
        _dragHighlight.Margin = new Thickness(left, 0, 0, 0);
        _dragHighlight.IsVisible = true;
    }

    // Nearest band boundary (0..bandCount) to an X position, in this panel's coordinates.
    private int BandBoundaryAt(double x)
    {
        var bestIndex = 0;
        var bestDist = double.MaxValue;
        for (var i = 0; i <= _bandHeaders.Count; i++)
        {
            var boundaryX = BoundaryX(i);
            var dist = Math.Abs(boundaryX - x);
            if (dist < bestDist)
            {
                bestDist = dist;
                bestIndex = i;
            }
        }
        return bestIndex;
    }

    private double BoundaryX(int boundaryIndex)
    {
        double x = 0;
        for (var i = 0; i < boundaryIndex && i < _bands.Count; i++)
            foreach (var c in _bands[i].Columns)
                x += c.Width;
        return x;
    }

    private void ShowDropLine(int boundaryIndex)
    {
        var x = BoundaryX(boundaryIndex);
        _dropLine.Margin = new Thickness(Math.Max(0, x - 1.5), 0, 0, 0);
        _dropLine.IsVisible = true;
    }

    private void HideDropLine() => _dropLine.IsVisible = false;

    // ── Resize grip ─────────────────────────────────────────────────────────────────────────────
    private void AddResizeGrip(SheetColumn column, int col, int rowSpan, int row = 0)
    {
        var grip = new Thumb
        {
            Width = GripWidth,
            Cursor = new Cursor(StandardCursorType.SizeWestEast),
            HorizontalAlignment = HorizontalAlignment.Right,
            Background = Brushes.Transparent,
            Classes = { "rosterResize" }
        };
        grip.DragDelta += (_, e) =>
        {
            var next = column.Width + e.Vector.X;
            column.Width = Math.Max(column.MinWidth, next);
        };
        // Persist once the drag finishes (not on every delta) so the file isn't hammered mid-resize.
        grip.DragCompleted += (_, _) => ColumnResized?.Invoke();
        SetColumn(grip, col);
        SetRow(grip, row);
        SetRowSpan(grip, rowSpan);
        Children.Add(grip);
    }
}

/// <summary>Converts a pixel double ↔ a fixed <see cref="GridLength"/> for column-width binding.</summary>
internal sealed class PixelToGridLength : Avalonia.Data.Converters.IValueConverter
{
    public static readonly PixelToGridLength Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => new GridLength(value is double d && d > 0 ? d : SheetColumn.DefaultWidth, GridUnitType.Pixel);

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => value is GridLength g ? g.Value : SheetColumn.DefaultWidth;
}
