using System.Collections.ObjectModel;
using System.Collections.Specialized;
using OrientPyx.Presentation.ViewModels.Pages;

namespace OrientPyx.Tests.Core;

/// <summary>
/// The live-refresh merge every table page uses (<see cref="RowSync"/>): rows survive as the same objects, a changed
/// item is applied in place, new items are inserted where the fresh list puts them, vanished ones are removed and
/// re-ordered ones are moved. Pure: no database, no UI.
/// </summary>
public sealed class RowSyncTests
{
    private sealed class Row(int key, string text)
    {
        public int Key { get; } = key;
        public string Text { get; set; } = text;
        public int Applied { get; private set; }

        public void Apply(string text)
        {
            Text = text;
            Applied++;
        }
    }

    private static ObservableCollection<Row> Rows(params (int Key, string Text)[] items)
        => new(items.Select(i => new Row(i.Key, i.Text)));

    private static (int Added, int Updated, int Removed) Sync(
        ObservableCollection<Row> rows, IReadOnlyList<(int Key, string Text)> items, List<Row>? removed = null)
        => RowSync.Sync(rows, items,
            itemKey: i => i.Key,
            rowKey: r => r.Key,
            create: i => new Row(i.Key, i.Text),
            update: (r, i) => r.Apply(i.Text),
            removed: removed is null ? null : removed.Add);

    [Fact]
    public void UpdateInPlace_KeepsTheSameRowObject()
    {
        var rows = Rows((1, "a"), (2, "b"));
        var first = rows[0];
        var second = rows[1];

        var counts = Sync(rows, [(1, "a"), (2, "B")]);

        Assert.Equal((0, 2, 0), counts);
        Assert.Same(first, rows[0]);
        Assert.Same(second, rows[1]);
        Assert.Equal("a", rows[0].Text);
        Assert.Equal("B", rows[1].Text);
        Assert.Equal(1, rows[1].Applied);
    }

    [Fact]
    public void Insert_LandsAtTheFreshListIndex()
    {
        var rows = Rows((1, "a"), (3, "c"));
        var first = rows[0];
        var third = rows[1];

        var counts = Sync(rows, [(1, "a"), (2, "b"), (3, "c"), (4, "d")]);

        Assert.Equal((2, 2, 0), counts);
        Assert.Equal([1, 2, 3, 4], rows.Select(r => r.Key));
        Assert.Same(first, rows[0]);
        Assert.Same(third, rows[2]);
    }

    [Fact]
    public void Remove_DropsVanishedRowsAndReportsThem()
    {
        var rows = Rows((1, "a"), (2, "b"), (3, "c"));
        var gone = rows[1];
        var removed = new List<Row>();

        var counts = Sync(rows, [(1, "a"), (3, "c")], removed);

        Assert.Equal((0, 2, 1), counts);
        Assert.Equal([1, 3], rows.Select(r => r.Key));
        Assert.Same(gone, Assert.Single(removed));
    }

    [Fact]
    public void Reorder_MovesRowsInsteadOfRecreatingThem()
    {
        var rows = Rows((1, "a"), (2, "b"), (3, "c"));
        var objects = rows.ToArray();
        var actions = new List<NotifyCollectionChangedAction>();
        rows.CollectionChanged += (_, e) => actions.Add(e.Action);

        var counts = Sync(rows, [(3, "c"), (1, "a"), (2, "b")]);

        Assert.Equal((0, 3, 0), counts);
        Assert.Equal([3, 1, 2], rows.Select(r => r.Key));
        Assert.Same(objects[2], rows[0]);
        Assert.Same(objects[0], rows[1]);
        Assert.Same(objects[1], rows[2]);
        Assert.All(actions, a => Assert.Equal(NotifyCollectionChangedAction.Move, a));
        Assert.NotEmpty(actions);
    }

    [Fact]
    public void UnchangedList_RaisesNoCollectionChange()
    {
        var rows = Rows((1, "a"), (2, "b"));
        var changes = 0;
        rows.CollectionChanged += (_, _) => changes++;

        Sync(rows, [(1, "a"), (2, "b")]);

        Assert.Equal(0, changes);
    }

    [Fact]
    public void DuplicateKeys_KeepTheFirstOccurrenceOnly()
    {
        var rows = Rows((1, "a"));

        var counts = Sync(rows, [(1, "first"), (1, "second"), (2, "b"), (2, "B")]);

        Assert.Equal((1, 1, 0), counts);
        Assert.Equal([1, 2], rows.Select(r => r.Key));
        Assert.Equal("first", rows[0].Text);
        Assert.Equal("b", rows[1].Text);
    }

    [Fact]
    public void EmptyCollection_IsFilledInOrder()
    {
        var rows = new ObservableCollection<Row>();

        var counts = Sync(rows, [(2, "b"), (1, "a")]);

        Assert.Equal((2, 0, 0), counts);
        Assert.Equal([2, 1], rows.Select(r => r.Key));
    }

    [Fact]
    public void EmptyItems_RemoveEverything()
    {
        var rows = Rows((1, "a"), (2, "b"));
        var removed = new List<Row>();

        var counts = Sync(rows, [], removed);

        Assert.Equal((0, 0, 2), counts);
        Assert.Empty(rows);
        Assert.Equal(2, removed.Count);
    }
}
