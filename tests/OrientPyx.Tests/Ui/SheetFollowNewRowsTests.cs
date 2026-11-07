using System.Collections.ObjectModel;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using OrientPyx.Presentation.Controls;
using OrientPyx.Tests.Support;

namespace OrientPyx.Tests.Ui;

/// <summary>
/// <see cref="SheetTable.FollowNewRows"/> (the finish read-out log): a table scrolled to the very bottom stays at the
/// bottom when a row is added, so the new read shows at once; a table scrolled anywhere else keeps its offset.
/// </summary>
[Collection(AppCollection.Name)]
public sealed class SheetFollowNewRowsTests(HeadlessApp app)
{
    public sealed class Row(string name)
    {
        public string Name { get; } = name;
    }

    [Fact]
    public async Task AddedRowKeepsABottomScrolledTableAtTheBottom()
    {
        var rows = new ObservableCollection<Row>(Enumerable.Range(1, 60).Select(i => new Row($"Рядок {i}")));
        var (window, table) = await ShowTableAsync(rows);
        try
        {
            var scroll = await app.OnUiAsync(() => BodyScroll(table));
            await app.OnUiAsync(() => scroll.Offset = new Vector(0, MaxY(scroll)));
            await app.SettleAsync();
            var before = await app.OnUiAsync(() => (scroll.Offset.Y, scroll.Extent.Height));
            Assert.True(before.Y > 0, "the table should overflow its viewport");

            await app.OnUiAsync(() => rows.Add(new Row("Новий")));
            await app.SettleAsync();
            await app.SettleAsync();

            var after = await app.OnUiAsync(() => (scroll.Offset.Y, scroll.Extent.Height, Max: MaxY(scroll)));
            Assert.True(after.Height > before.Height, "the extent should grow by the new row");
            Assert.True(after.Y >= after.Max - 1, $"the table left the bottom: offset {after.Y}, max {after.Max}");
        }
        finally
        {
            await app.OnUiAsync(window.Close);
        }
    }

    [Fact]
    public async Task AddedRowLeavesAMidScrolledTableWhereItWas()
    {
        var rows = new ObservableCollection<Row>(Enumerable.Range(1, 60).Select(i => new Row($"Рядок {i}")));
        var (window, table) = await ShowTableAsync(rows);
        try
        {
            var scroll = await app.OnUiAsync(() => BodyScroll(table));
            await app.OnUiAsync(() => scroll.Offset = new Vector(0, MaxY(scroll) / 2));
            await app.SettleAsync();
            var before = await app.OnUiAsync(() => scroll.Offset.Y);

            await app.OnUiAsync(() => rows.Add(new Row("Новий")));
            await app.SettleAsync();
            await app.SettleAsync();

            var after = await app.OnUiAsync(() => scroll.Offset.Y);
            Assert.Equal(before, after, precision: 3);
        }
        finally
        {
            await app.OnUiAsync(window.Close);
        }
    }

    private async Task<(Window Window, SheetTable Table)> ShowTableAsync(ObservableCollection<Row> rows)
    {
        var shown = await app.OnUiAsync(() =>
        {
            var localization = app.Vm.Localization;
            var table = new SheetTable { Localization = localization, FollowNewRows = true, ItemsSource = rows };
            table.Bands = new SheetColumnBuilder(localization).Text("FinishRead.Col.FullName", nameof(Row.Name)).Bands;
            var window = new Window { Width = 600, Height = 320, Content = table };
            window.Show();
            return (window, table);
        });
        await app.SettleAsync();
        await app.SettleAsync();
        return shown;
    }

    private static double MaxY(ScrollViewer scroll) => Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);

    private static ScrollViewer BodyScroll(SheetTable table)
    {
        var field = typeof(SheetTable).GetField("_bodyScroll", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (ScrollViewer)field.GetValue(table)!;
    }
}
