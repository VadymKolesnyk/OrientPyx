using System.Collections.ObjectModel;

namespace OrientPyx.Presentation.ViewModels.Pages;

/// <summary>
/// Helpers for keeping a grid's selection sensible while a row is deleted. Used by the
/// spreadsheet-like pages so that deleting the focused row moves focus to the next row
/// (or the previous one if the last row was removed) instead of clearing the selection.
/// </summary>
internal static class GridSelection
{
    /// <summary>
    /// Returns the row that should be selected after <paramref name="row"/> is removed from
    /// <paramref name="items"/>: the row that currently follows it, or the one before it when
    /// it is last, or <c>null</c> when it is the only row. Returns <c>null</c> if the row is
    /// not in the collection.
    /// </summary>
    public static T? NeighbourAfterRemoval<T>(ObservableCollection<T> items, T row)
        where T : class
    {
        var index = items.IndexOf(row);
        if (index < 0)
            return null;

        if (index + 1 < items.Count)
            return items[index + 1];
        if (index - 1 >= 0)
            return items[index - 1];
        return null;
    }
}
