using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using OrientDesk.Presentation.ViewModels.Pages;

namespace OrientDesk.Presentation.Views.Pages;

public partial class DrawView : UserControl
{
    // In-process drag payload: the group chip being dragged between/within start-group columns. An
    // in-process format keeps the actual view-model reference (never serialized to the OS clipboard).
    private static readonly DataFormat<DrawGroupItemViewModel> GroupFormat =
        DataFormat.CreateInProcessFormat<DrawGroupItemViewModel>("orientdesk-draw-group");

    public DrawView()
    {
        InitializeComponent();
    }

    private DrawViewModel? Vm => DataContext as DrawViewModel;

    // ── Drag start ───────────────────────────────────────────────────────────────────────────────────

    private async void OnChipPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { DataContext: DrawGroupItemViewModel item })
            return;
        if (!e.GetCurrentPoint(sender as Visual).Properties.IsLeftButtonPressed)
            return;
        // Don't hijack clicks on the move buttons inside the chip — let them work as before.
        if (e.Source is Visual src && src.FindAncestorOfType<Button>(includeSelf: true) is not null)
            return;
        if (Vm is not { } vm)
            return;

        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(GroupFormat, item));

        vm.BeginDrag(item); // render the dragged chip semi-transparent
        try
        {
            await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move);
        }
        finally
        {
            vm.EndDrag(); // clear transparency + any insertion line / column highlight
        }
    }

    // ── Drag over (effect + insertion indicator) ───────────────────────────────────────────────────────

    private static bool TryGetDragged(DragEventArgs e, out DrawGroupItemViewModel item)
    {
        item = e.DataTransfer.TryGetValue(GroupFormat)!;
        return item is not null;
    }

    // All drop-target logic lives on the column (the chips are not drop targets) so there is a single
    // source of truth for the insertion index. The index is computed from the pointer's Y against each
    // chip's vertical midpoint, which works the same whether the cursor is over a chip or in a between-gap
    // — no chip/column disagreement, and (with the fixed-height gaps in XAML) the line never reflows.
    private void OnColumnDragOver(object? sender, DragEventArgs e)
    {
        if (sender is not Border { DataContext: DrawStartGroupViewModel column } columnBorder ||
            Vm is not { } vm || !TryGetDragged(e, out _))
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        vm.SetDropIndicator(column, InsertionIndex(columnBorder, column, e));
        e.DragEffects = DragDropEffects.Move;
        e.Handled = true;
    }

    // DragLeave bubbles up from inner children too, so it fires constantly while the pointer is still
    // moving WITHIN the column — clearing the indicator on every one of those caused the line/highlight to
    // blink. Only clear when the pointer is genuinely outside this column's bounds (a real exit); a re-enter
    // or another column's DragOver re-sets it anyway.
    private void OnColumnDragLeave(object? sender, DragEventArgs e)
    {
        if (sender is not Border columnBorder)
            return;

        var p = e.GetPosition(columnBorder);
        var inside = p.X >= 0 && p.Y >= 0 && p.X <= columnBorder.Bounds.Width && p.Y <= columnBorder.Bounds.Height;
        if (!inside)
            Vm?.SetDropIndicator(null, -1);
    }

    // ── Drop ─────────────────────────────────────────────────────────────────────────────────────────

    private void OnColumnDrop(object? sender, DragEventArgs e)
    {
        if (sender is not Border { DataContext: DrawStartGroupViewModel column } columnBorder ||
            Vm is not { } vm || !TryGetDragged(e, out var dragged))
            return;

        vm.MoveGroupTo(dragged, column, InsertionIndex(columnBorder, column, e));
        e.Handled = true;
    }

    // ── Shared insertion-index math ────────────────────────────────────────────────────────────────────

    // Returns where a drop at the current pointer position would insert into the column: the index of the
    // first chip whose vertical midpoint is below the pointer, or Groups.Count when the pointer is below
    // them all. Computed from the realized chip borders (named "ChipBorder") in this column, so it's a
    // single, gap-agnostic source of truth used by both DragOver (line) and Drop (move).
    private static int InsertionIndex(Visual columnBorder, DrawStartGroupViewModel column, DragEventArgs e)
    {
        var chips = columnBorder.GetVisualDescendants()
            .OfType<Border>()
            .Where(b => b.Name == "ChipBorder" && b.DataContext is DrawGroupItemViewModel)
            .ToList();
        if (chips.Count == 0)
            return 0;

        for (var i = 0; i < chips.Count; i++)
        {
            var chip = chips[i];
            var mid = chip.TranslatePoint(new Point(0, chip.Bounds.Height / 2), columnBorder)?.Y;
            if (mid is { } m && e.GetPosition(columnBorder).Y < m)
            {
                // Map this realized chip back to its model index (containers can be reordered/recycled).
                return chip.DataContext is DrawGroupItemViewModel vm ? column.Groups.IndexOf(vm) : i;
            }
        }
        return column.Groups.Count;
    }
}
