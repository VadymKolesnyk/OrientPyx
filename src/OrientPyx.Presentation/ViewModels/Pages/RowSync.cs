using System.Collections.ObjectModel;

namespace OrientPyx.Presentation.ViewModels.Pages;

/// <summary>
/// Merges fresh data into an existing row collection instead of clearing and refilling it: a row whose key is still
/// present is updated in place (only the cells whose value changed raise a property change), a new key gets a new
/// row, a vanished key is removed, and the order follows the fresh list with <see cref="ObservableCollection{T}.Move"/>.
/// The table's virtualised containers therefore stay bound to the same row objects — a live refresh after another
/// station's write changes exactly the cells that changed, with no flicker and no lost selection or scroll.
/// </summary>
public static class RowSync
{
    /// <summary>
    /// Syncs <paramref name="rows"/> to <paramref name="items"/>.
    /// </summary>
    /// <param name="rows">The bound collection (must be touched on the UI thread).</param>
    /// <param name="items">The fresh data, in the order the table should show.</param>
    /// <param name="itemKey">The identity of a data item.</param>
    /// <param name="rowKey">The identity of a row.</param>
    /// <param name="create">Builds a row for a new item.</param>
    /// <param name="update">Applies an item onto its existing row (the row must raise changes only for cells that differ and
    /// must not trigger its autosave while being applied).</param>
    /// <param name="removed">Called for every row dropped (to unhook its events).</param>
    /// <returns>How many rows were added, updated and removed.</returns>
    public static (int Added, int Updated, int Removed) Sync<TRow, TItem, TKey>(
        ObservableCollection<TRow> rows,
        IReadOnlyList<TItem> items,
        Func<TItem, TKey> itemKey,
        Func<TRow, TKey> rowKey,
        Func<TItem, TRow> create,
        Action<TRow, TItem> update,
        Action<TRow>? removed = null)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(items);

        var byKey = new Dictionary<TKey, TRow>(rows.Count);
        foreach (var row in rows)
            byKey.TryAdd(rowKey(row), row);

        // 1. Remove rows whose key is gone (from the end, so indexes stay valid).
        var wanted = new HashSet<TKey>(items.Count);
        foreach (var item in items)
            wanted.Add(itemKey(item));
        var removedCount = 0;
        for (var i = rows.Count - 1; i >= 0; i--)
        {
            if (wanted.Contains(rowKey(rows[i])))
                continue;
            var row = rows[i];
            rows.RemoveAt(i);
            byKey.Remove(rowKey(row));
            removed?.Invoke(row);
            removedCount++;
        }

        // 2. Walk the fresh list: update or insert at the target index, moving an existing row into place.
        var added = 0;
        var updated = 0;
        var seen = new HashSet<TKey>(items.Count);
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var key = itemKey(item);
            if (!seen.Add(key))
                continue; // a duplicate key in the data: keep the first occurrence only

            if (byKey.TryGetValue(key, out var row))
            {
                update(row, item);
                updated++;
                var current = rows.IndexOf(row);
                if (current != index && index < rows.Count)
                    rows.Move(current, index);
            }
            else
            {
                row = create(item);
                byKey[key] = row;
                if (index <= rows.Count)
                    rows.Insert(index, row);
                else
                    rows.Add(row);
                added++;
            }
        }

        return (added, updated, removedCount);
    }
}
