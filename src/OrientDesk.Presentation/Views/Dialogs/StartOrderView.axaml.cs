using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using OrientDesk.Presentation.ViewModels.Dialogs;

namespace OrientDesk.Presentation.Views.Dialogs;

public partial class StartOrderView : UserControl
{
    // In-process drag payload: the member row being dragged within the group's list. An in-process format
    // keeps the actual view-model reference (never serialized to the OS clipboard). Mirrors DrawView.
    private static readonly DataFormat<StartOrderMemberViewModel> MemberFormat =
        DataFormat.CreateInProcessFormat<StartOrderMemberViewModel>("orientdesk-start-order-member");

    public StartOrderView()
    {
        InitializeComponent();
    }

    private StartOrderViewModel? Vm => DataContext as StartOrderViewModel;

    // ── Drag start ───────────────────────────────────────────────────────────────────────────────────

    private async void OnRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { DataContext: StartOrderMemberViewModel item } row)
            return;
        if (!e.GetCurrentPoint(row).Properties.IsLeftButtonPressed)
            return;
        if (Vm is not { } vm)
            return;

        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(MemberFormat, item));

        vm.BeginDrag(item); // fade the dragged row
        try
        {
            await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move);
        }
        finally
        {
            vm.EndDrag(); // clear fade + any insertion line
        }
    }

    // ── Drag over (insertion-line preview) ───────────────────────────────────────────────────────────

    private static bool TryGetDragged(DragEventArgs e, out StartOrderMemberViewModel item)
    {
        item = e.DataTransfer.TryGetValue(MemberFormat)!;
        return item is not null;
    }

    // The whole list (the ScrollViewer) is the single drop surface, so there is one source of truth for the
    // insertion index — computed from the pointer's Y against each realized row's vertical midpoint. This
    // matches DrawView and keeps the line from disagreeing between rows and gaps.
    private void OnListDragOver(object? sender, DragEventArgs e)
    {
        if (sender is not Visual surface || Vm is not { } vm || !TryGetDragged(e, out _))
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        vm.SetDropIndicator(InsertionIndex(surface, e));
        e.DragEffects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void OnListDragLeave(object? sender, DragEventArgs e)
    {
        if (sender is not Visual surface)
            return;

        // DragLeave bubbles from inner children too; only clear when the pointer truly left the surface.
        var p = e.GetPosition(surface);
        var inside = p.X >= 0 && p.Y >= 0 && p.X <= surface.Bounds.Width && p.Y <= surface.Bounds.Height;
        if (!inside)
            Vm?.ClearDropIndicator();
    }

    // ── Drop ─────────────────────────────────────────────────────────────────────────────────────────

    private void OnListDrop(object? sender, DragEventArgs e)
    {
        if (sender is not Visual surface || Vm is not { } vm || !TryGetDragged(e, out var dragged))
            return;

        vm.MoveTo(dragged, InsertionIndex(surface, e));
        e.Handled = true;
    }

    // ── Shared insertion-index math ────────────────────────────────────────────────────────────────────

    // Returns where a drop at the current pointer position would insert: the index of the first row whose
    // vertical midpoint is below the pointer, or the member count when the pointer is below them all. Computed
    // from the realized row borders (named "MemberRow"), mapped back to their model index (containers recycle).
    private int InsertionIndex(Visual surface, DragEventArgs e)
    {
        var members = Vm?.Members;
        if (members is null || members.Count == 0)
            return 0;

        var rows = surface.GetVisualDescendants()
            .OfType<Border>()
            .Where(b => b.Name == "MemberRow" && b.DataContext is StartOrderMemberViewModel)
            .ToList();

        var y = e.GetPosition(surface).Y;
        foreach (var row in rows)
        {
            var mid = row.TranslatePoint(new Point(0, row.Bounds.Height / 2), surface)?.Y;
            if (mid is { } m && y < m)
                return row.DataContext is StartOrderMemberViewModel vm ? members.IndexOf(vm) : 0;
        }
        return members.Count;
    }
}
