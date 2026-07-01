using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using OrientDesk.BusinessLogic.Models;
using OrientDesk.Presentation.ViewModels.Pages;

namespace OrientDesk.Presentation.Views.Pages;

/// <summary>
/// Renders an <see cref="OnlinePreviewViewModel"/> into a host <see cref="Grid"/> as a light-theme mock-up of the
/// online spectator results table: one section per group — a caption, a grey column-header row, and zebra-striped
/// data rows (unplaced runners greyed). It shows the LARGE-screen column set; a column also hidden on the phone
/// carries a small 📱 badge in its header so the effect of the «Мал.» toggle is visible without a second table.
/// Every header cell is a drag source + drop target, so the column order can be changed by dragging (the dragged +
/// hovered columns are washed and an insertion line marks the landing spot), calling back into the owning
/// <see cref="OnlineResultsViewModel"/>.
/// </summary>
public sealed class OnlinePreviewTable
{
    private static readonly DataFormat<string> ColumnFormat =
        DataFormat.CreateInProcessFormat<string>("orientdesk-online-preview-column");

    // A light spectator-style palette (kept simple; the real frontend has its own CSS).
    private static readonly IBrush Line = Hex(0xE2, 0xE8, 0xF0);
    private static readonly IBrush Text = Hex(0x16, 0x20, 0x2F);
    private static readonly IBrush Muted = Hex(0x5A, 0x6B, 0x86);
    private static readonly IBrush Accent = Hex(0x1F, 0x6F, 0xEB);
    private static readonly IBrush Caption = Hex(0x0F, 0x2C, 0x52);
    private static readonly IBrush Head = Hex(0xED, 0xF1, 0xF7);
    private static readonly IBrush Row1 = Hex(0xFF, 0xFF, 0xFF);
    private static readonly IBrush Row2 = Hex(0xF6, 0xF8, 0xFC);
    private static readonly IBrush Dim = Hex(0x9A, 0xA6, 0xBA);

    private static readonly IBrush DropColumnFill = new SolidColorBrush(Color.FromArgb(0x33, 0x1F, 0x6F, 0xEB));
    private static readonly IBrush DraggedColumnFill = new SolidColorBrush(Color.FromArgb(0x1E, 0x1F, 0x6F, 0xEB));
    private static readonly IBrush DropLineBrush = Accent;

    private const double BodyFontSize = 14;
    private const double HeaderFontSize = 11;
    private const double CaptionFontSize = 15;

    private readonly Grid _host;
    private OnlineResultsViewModel? _vm;

    // All cells of each column (header + body) so a drag can wash a whole column. Index = column index.
    private readonly List<List<Border>> _columnCells = [];
    private Rectangle? _dropLine;

    public OnlinePreviewTable(Grid host) => _host = host;

    /// <summary>Points the table at the page VM (or null), (re)subscribing to its preview's single Changed event
    /// (raised once per refresh, after both column + section collections are repopulated).</summary>
    public void Bind(OnlineResultsViewModel? vm)
    {
        if (_vm is { } old)
            old.Preview.Changed -= OnPreviewChanged;
        _vm = vm;
        if (_vm is { } v)
            v.Preview.Changed += OnPreviewChanged;
        Rebuild();
    }

    private void OnPreviewChanged(object? sender, EventArgs e) => Rebuild();

    private void Rebuild()
    {
        _host.Children.Clear();
        _host.ColumnDefinitions.Clear();
        _host.RowDefinitions.Clear();
        _columnCells.Clear();
        _dropLine = null;

        if (_vm is not { } vm)
            return;
        var columns = vm.Preview.Columns;
        var sections = vm.Preview.Sections;
        if (columns.Count == 0 || sections.Count == 0)
            return;

        // Content-sized columns: free-text (name/club/region/team/qual) stretch (star), short codes auto.
        for (var c = 0; c < columns.Count; c++)
            _host.ColumnDefinitions.Add(new ColumnDefinition(
                IsStretchy(columns[c].Column) ? new GridLength(1, GridUnitType.Star) : GridLength.Auto));

        for (var c = 0; c < columns.Count; c++)
            _columnCells.Add([]);

        var row = 0;
        foreach (var section in sections)
        {
            // Group caption spanning all columns — like the frontend's <div class="ghead">.
            _host.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            AddGroupCaption(section.Name, section.Caption, row, columns.Count, topMargin: row == 0 ? 0 : 16);
            row++;

            // Column header row — grey band; each cell drags.
            _host.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (var c = 0; c < columns.Count; c++)
            {
                var header = BuildHeaderCell(columns[c]);
                WireDrag(header);
                Grid.SetColumn(header, c);
                Grid.SetRow(header, row);
                _host.Children.Add(header);
                _columnCells[c].Add(header);
            }
            row++;

            // Zebra-striped data rows; unplaced runners dimmed.
            var r = 0;
            foreach (var bodyRow in section.Rows)
            {
                _host.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var stripe = r % 2 == 0 ? Row1 : Row2;
                for (var c = 0; c < columns.Count; c++)
                {
                    var value = c < bodyRow.Values.Count ? bodyRow.Values[c] : string.Empty;
                    var cell = BuildBodyCell(value, columns[c], bodyRow.Unplaced, stripe);
                    WireDrag(cell);
                    Grid.SetColumn(cell, c);
                    Grid.SetRow(cell, row);
                    _host.Children.Add(cell);
                    _columnCells[c].Add(cell);
                }
                row++;
                r++;
            }
        }

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

    // Free-text columns get the leftover width; short-code columns size to content.
    private static bool IsStretchy(ResultColumn column) => column switch
    {
        ResultColumn.FullName or ResultColumn.Team or ResultColumn.Club or ResultColumn.Region => true,
        _ => false,
    };

    // Name-ish columns left-align; everything else (place/number/year/time/status/points) centres.
    private static bool IsLeftAligned(ResultColumn column) => column switch
    {
        ResultColumn.FullName or ResultColumn.Team or ResultColumn.Club or ResultColumn.Region => true,
        _ => false,
    };

    private void AddGroupCaption(string name, string facts, int row, int columnCount, double topMargin)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Margin = new Thickness(0, topMargin, 0, 4)
        };
        panel.Children.Add(new TextBlock
        {
            Text = name,
            Foreground = Caption,
            FontWeight = FontWeight.Bold,
            FontSize = CaptionFontSize,
            VerticalAlignment = VerticalAlignment.Center
        });
        if (!string.IsNullOrWhiteSpace(facts))
            panel.Children.Add(new TextBlock
            {
                Text = facts,
                Foreground = Muted,
                FontSize = CaptionFontSize * 0.8,
                VerticalAlignment = VerticalAlignment.Center
            });

        Grid.SetColumn(panel, 0);
        Grid.SetColumnSpan(panel, columnCount);
        Grid.SetRow(panel, row);
        _host.Children.Add(panel);
    }

    private static Border BuildHeaderCell(OnlinePreviewColumn col)
    {
        var text = new TextBlock
        {
            Text = col.Header ?? string.Empty,
            Foreground = Muted,
            FontWeight = FontWeight.SemiBold,
            FontSize = HeaderFontSize,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.NoWrap
        };

        Control content = text;
        // Badge a column that is hidden on the phone, so the «Мал.» toggle's effect shows in the preview.
        if (!col.ShownOnSmall)
        {
            var stack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 3,
                HorizontalAlignment = IsLeftAligned(col.Column) ? HorizontalAlignment.Left : HorizontalAlignment.Center
            };
            stack.Children.Add(text);
            stack.Children.Add(new TextBlock
            {
                Text = "📱",
                FontSize = HeaderFontSize - 1,
                Opacity = 0.45,
                VerticalAlignment = VerticalAlignment.Center
            });
            content = stack;
        }
        else
        {
            text.HorizontalAlignment = IsLeftAligned(col.Column)
                ? HorizontalAlignment.Left : HorizontalAlignment.Center;
        }

        return new Border
        {
            Background = Head,
            Tag = Head, // base brush, restored after a drag wash
            BorderBrush = Line,
            BorderThickness = new Thickness(0, 0, 0, 2),
            Padding = new Thickness(8, 5),
            Cursor = new Cursor(StandardCursorType.SizeAll),
            DataContext = col,
            Child = content
        };
    }

    private static Border BuildBodyCell(string text, OnlinePreviewColumn col, bool unplaced, IBrush stripe) => new()
    {
        Background = stripe,
        Tag = stripe, // base brush, restored after a drag wash
        BorderBrush = Line,
        BorderThickness = new Thickness(0, 0, 0, 1),
        Padding = new Thickness(8, 4),
        Cursor = new Cursor(StandardCursorType.SizeAll),
        DataContext = col,
        Child = new TextBlock
        {
            Text = text,
            Foreground = unplaced ? Dim : Text,
            // Place + name are bold; the rest normal.
            FontWeight = col.Column is ResultColumn.Place or ResultColumn.FullName ? FontWeight.SemiBold : FontWeight.Normal,
            FontSize = BodyFontSize,
            HorizontalAlignment = IsLeftAligned(col.Column) ? HorizontalAlignment.Left : HorizontalAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        }
    };

    // ── Column drag-reorder (from any cell in a column) ──────────────────────────────────────────────────

    private void WireDrag(Border cell)
    {
        cell.PointerPressed += OnPointerPressed;
        cell.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        cell.AddHandler(DragDrop.DropEvent, OnDrop);
        DragDrop.SetAllowDrop(cell, true);
    }

    private async void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { DataContext: OnlinePreviewColumn col } cell)
            return;
        if (!e.GetCurrentPoint(cell).Properties.IsLeftButtonPressed)
            return;

        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(ColumnFormat, col.Key));

        TintDragged(col.Key);
        try
        {
            await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move);
        }
        finally
        {
            ClearHighlight();
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (sender is not Border { DataContext: OnlinePreviewColumn target } cell ||
            _vm is not { } vm || !TryGetDragged(e, out var draggedKey))
        {
            e.DragEffects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.DragEffects = DragDropEffects.Move;
        e.Handled = true;

        var targetIndex = IndexOfColumn(vm, target.Key);
        if (targetIndex < 0)
            return;
        var insertAfter = e.GetPosition(cell).X > cell.Bounds.Width / 2;
        Highlight(draggedKey, targetIndex, insertAfter);
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        ClearHighlight();
        if (sender is not Border { DataContext: OnlinePreviewColumn target } cell ||
            _vm is not { } vm || !TryGetDragged(e, out var draggedKey))
            return;

        var insertAfter = e.GetPosition(cell).X > cell.Bounds.Width / 2;
        vm.MoveColumnByKey(draggedKey, target.Key, insertAfter);
        e.Handled = true;
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

    private static int IndexOfColumn(OnlineResultsViewModel vm, string key)
    {
        for (var i = 0; i < vm.Preview.Columns.Count; i++)
            if (vm.Preview.Columns[i].Key == key)
                return i;
        return -1;
    }

    private void TintDragged(string draggedKey)
    {
        if (_vm is not { } vm)
            return;
        ClearHighlight();
        var dragged = IndexOfColumn(vm, draggedKey);
        if (dragged >= 0 && dragged < _columnCells.Count)
            FillColumn(dragged, DraggedColumnFill);
    }

    private void Highlight(string draggedKey, int targetIndex, bool insertAfter)
    {
        if (_vm is not { } vm)
            return;
        ClearHighlight();

        var dragged = IndexOfColumn(vm, draggedKey);
        if (dragged >= 0 && dragged < _columnCells.Count)
            FillColumn(dragged, DraggedColumnFill);
        if (targetIndex >= 0 && targetIndex < _columnCells.Count)
            FillColumn(targetIndex, DropColumnFill);

        if (_dropLine is { } line)
        {
            Grid.SetColumn(line, targetIndex);
            line.HorizontalAlignment = insertAfter ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            line.Margin = insertAfter ? new Thickness(0, 0, -1.5, 0) : new Thickness(-1.5, 0, 0, 0);
            line.IsVisible = true;
        }
    }

    private void FillColumn(int columnIndex, IBrush wash)
    {
        // Replace each cell's background with the drag wash; the base stripe/head brush is kept in Tag so
        // ClearHighlight can restore it without a full rebuild (a rebuild mid-drag would drop the live cells).
        foreach (var cell in _columnCells[columnIndex])
            cell.Background = wash;
    }

    private void ClearHighlight()
    {
        foreach (var column in _columnCells)
            foreach (var cell in column)
                if (cell.Tag is IBrush baseBrush)
                    cell.Background = baseBrush;
        if (_dropLine is { } line)
            line.IsVisible = false;
    }

    private static IBrush Hex(byte r, byte g, byte b) => new SolidColorBrush(Color.FromRgb(r, g, b));
}
