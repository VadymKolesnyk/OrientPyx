using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Layout;
using OrientDesk.Presentation.ViewModels.Pages;

namespace OrientDesk.Presentation.Views.Pages;

/// <summary>
/// Renders the shared protocol document preview into a host <see cref="Grid"/>: a header row of column
/// captions (each a drag source + drop target so columns can be reordered) and one row per preview body
/// row, with aligned, code-sized columns. Used by both the results-protocol and start-protocol pages — they
/// each own a <see cref="ProtocolPreviewTable"/> bound to their <see cref="IProtocolPreviewHost"/> and the
/// "PreviewTableHost" grid in their XAML. Built in code (not XAML) because the column set is dynamic and the
/// headers carry the drag interaction. Rebuilds whenever the host's preview columns/rows change.
/// </summary>
public sealed class ProtocolPreviewTable
{
    // In-process drag payload: the dragged column's key (its enum name). DataFormat needs a reference type,
    // so the key travels as a string; in-process keeps it local (never serialized to the OS clipboard).
    private static readonly DataFormat<string> ColumnFormat =
        DataFormat.CreateInProcessFormat<string>("orientdesk-protocol-column");

    private static readonly IBrush GridLine = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
    private static readonly IBrush HeaderFill = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0));

    private readonly Grid _host;
    private IProtocolPreviewHost? _previewHost;

    public ProtocolPreviewTable(Grid host)
    {
        _host = host;
    }

    /// <summary>Points the table at a new host VM (or null), (re)subscribing to its preview collections.</summary>
    public void Bind(IProtocolPreviewHost? host)
    {
        if (_previewHost is { } old)
        {
            old.Preview.Columns.CollectionChanged -= OnPreviewChanged;
            old.Preview.Rows.CollectionChanged -= OnPreviewChanged;
        }
        _previewHost = host;
        if (_previewHost is { } h)
        {
            h.Preview.Columns.CollectionChanged += OnPreviewChanged;
            h.Preview.Rows.CollectionChanged += OnPreviewChanged;
        }
        Rebuild();
    }

    private void OnPreviewChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    private void Rebuild()
    {
        _host.Children.Clear();
        _host.ColumnDefinitions.Clear();
        _host.RowDefinitions.Clear();

        if (_previewHost is not { } host)
            return;
        var columns = host.Preview.Columns;
        var rows = host.Preview.Rows;
        if (columns.Count == 0)
            return;

        for (var c = 0; c < columns.Count; c++)
            _host.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        _host.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); // header
        for (var r = 0; r < rows.Count; r++)
            _host.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        // Header cells — each is a drag source + drop target for reordering its column.
        for (var c = 0; c < columns.Count; c++)
        {
            var col = columns[c];
            var header = new Border
            {
                Background = HeaderFill,
                BorderBrush = GridLine,
                BorderThickness = new Thickness(0.5),
                Padding = new Thickness(8, 5),
                Cursor = new Cursor(StandardCursorType.SizeWestEast),
                DataContext = col,
                Child = new TextBlock
                {
                    Text = col.Caption,
                    Foreground = Brushes.Black,
                    FontWeight = FontWeight.SemiBold,
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            header.PointerPressed += OnHeaderPointerPressed;
            header.AddHandler(DragDrop.DragOverEvent, OnHeaderDragOver);
            header.AddHandler(DragDrop.DropEvent, OnHeaderDrop);
            DragDrop.SetAllowDrop(header, true);

            Grid.SetColumn(header, c);
            Grid.SetRow(header, 0);
            _host.Children.Add(header);
        }

        // Body cells.
        for (var r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            for (var c = 0; c < columns.Count; c++)
            {
                var text = c < row.Cells.Count ? row.Cells[c] : string.Empty;
                var cell = new Border
                {
                    BorderBrush = GridLine,
                    BorderThickness = new Thickness(0.5),
                    Padding = new Thickness(8, 4),
                    Child = new TextBlock
                    {
                        Text = text,
                        Foreground = Brushes.Black,
                        FontSize = 11,
                        FontWeight = row.IsTeamHeader ? FontWeight.Bold : FontWeight.Normal,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                };
                Grid.SetColumn(cell, c);
                Grid.SetRow(cell, r + 1);
                _host.Children.Add(cell);
            }
        }
    }

    // ── Header drag-reorder ──────────────────────────────────────────────────────────────────────────────

    private async void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { DataContext: ProtocolPreviewColumn col } header)
            return;
        if (!e.GetCurrentPoint(header).Properties.IsLeftButtonPressed)
            return;

        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(ColumnFormat, col.Key));
        await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move);
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

    private void OnHeaderDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = TryGetDragged(e, out _) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    // Drop onto a header column: move the dragged column to this column's index. Dropping on the right half
    // inserts after it, so the user can move a column to the very end.
    private void OnHeaderDrop(object? sender, DragEventArgs e)
    {
        if (sender is not Border { DataContext: ProtocolPreviewColumn target } header ||
            _previewHost is not { } host || !TryGetDragged(e, out var draggedKey))
            return;

        var targetIndex = IndexOfColumn(host, target.Key);
        if (targetIndex < 0)
            return;

        if (e.GetPosition(header).X > header.Bounds.Width / 2)
            targetIndex++;

        var fromIndex = IndexOfColumn(host, draggedKey);
        if (fromIndex >= 0 && fromIndex < targetIndex)
            targetIndex--;

        host.MoveColumnByKey(draggedKey, targetIndex);
        e.Handled = true;
    }

    private static int IndexOfColumn(IProtocolPreviewHost host, string key)
    {
        for (var i = 0; i < host.Preview.Columns.Count; i++)
            if (host.Preview.Columns[i].Key == key)
                return i;
        return -1;
    }
}
