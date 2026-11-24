using System;
using Avalonia.Threading;
using OrientPyx.Presentation.ViewModels.Pages;

namespace OrientPyx.Presentation.Controls;

/// <summary>
/// Shows a page's pending reveal («Перейти» from the day readiness check) on its table: takes it from the page and,
/// once the layout has settled, selects the row, scrolls to it and focuses the cell (<see cref="SheetTable.RevealRow"/>).
/// A view calls it when attached and on <see cref="PageViewModelBase.RevealRequested"/>; whichever comes first takes it.
/// </summary>
public static class SheetReveal
{
    // A table built a moment ago may not have its body or rows yet: try a few layout passes later.
    private const int Attempts = 3;

    public static void Apply(PageViewModelBase? page, SheetTable table, Func<Guid, object?> findRow)
    {
        if (page?.ConsumePendingReveal() is { } reveal)
            Show(table, findRow, reveal, Attempts);
    }

    private static void Show(SheetTable table, Func<Guid, object?> findRow, PendingReveal reveal, int attempts) =>
        Dispatcher.UIThread.Post(() =>
        {
            var row = findRow(reveal.Id);
            if ((row is null || !table.RevealRow(row, reveal.ColumnKey)) && attempts > 1)
                Show(table, findRow, reveal, attempts - 1);
        }, DispatcherPriority.Background);
}
