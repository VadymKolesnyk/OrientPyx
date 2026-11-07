using System.Collections.ObjectModel;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using OrientPyx.Presentation.Controls;
using OrientPyx.Tests.Support;

namespace OrientPyx.Tests.Ui;

/// <summary>
/// The frozen header is slaved to the body's horizontal offset through its own (scrollbar-hidden) ScrollViewer.
/// A horizontal wheel over the header must scroll the body — and the header with it — instead of moving the
/// header alone out of line with its columns.
/// </summary>
[Collection(AppCollection.Name)]
public sealed class SheetHeaderWheelTests(HeadlessApp app)
{
    public sealed class Row(string name)
    {
        public string Name { get; } = name;
    }

    [Theory]
    [InlineData(true)]   // Shift + vertical wheel
    [InlineData(false)]  // touchpad swipe / tilt wheel: a horizontal delta
    public async Task HorizontalWheelOverTheHeaderScrollsTheBodyAndKeepsTheHeaderInLine(bool shift)
    {
        var rows = new ObservableCollection<Row>(Enumerable.Range(1, 40).Select(i => new Row($"Рядок {i}")));
        var (window, table) = await ShowTableAsync(rows);
        try
        {
            var (body, header) = await app.OnUiAsync(() => (Scroll(table, "_bodyScroll"), Scroll(table, "_headerScroll")));
            var point = await app.OnUiAsync(() =>
                header.TranslatePoint(new Point(header.Bounds.Width / 2, header.Bounds.Height / 2), window)!.Value);

            var delta = shift ? new Vector(0, -2) : new Vector(-2, 0);
            var modifiers = shift ? RawInputModifiers.Shift : RawInputModifiers.None;
            await app.OnUiAsync(() => window.MouseWheel(point, delta, modifiers));
            await app.SettleAsync();
            await app.SettleAsync();

            var (bodyX, headerX) = await app.OnUiAsync(() => (body.Offset.X, header.Offset.X));
            Assert.True(bodyX > 0, $"the body did not scroll horizontally (header at {headerX})");
            Assert.Equal(bodyX, headerX, precision: 3);
        }
        finally
        {
            await app.OnUiAsync(window.Close);
        }
    }

    [Fact]
    public async Task VerticalWheelOverTheHeaderScrollsTheRows()
    {
        var rows = new ObservableCollection<Row>(Enumerable.Range(1, 40).Select(i => new Row($"Рядок {i}")));
        var (window, table) = await ShowTableAsync(rows);
        try
        {
            var (body, header) = await app.OnUiAsync(() => (Scroll(table, "_bodyScroll"), Scroll(table, "_headerScroll")));
            var point = await app.OnUiAsync(() =>
                header.TranslatePoint(new Point(header.Bounds.Width / 2, header.Bounds.Height / 2), window)!.Value);

            await app.OnUiAsync(() => window.MouseWheel(point, new Vector(0, -2)));
            await app.SettleAsync();
            await app.SettleAsync();

            var (bodyY, headerX) = await app.OnUiAsync(() => (body.Offset.Y, header.Offset.X));
            Assert.True(bodyY > 0, "the wheel over the header did not scroll the rows");
            Assert.Equal(0, headerX, precision: 3);
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
            var table = new SheetTable { Localization = localization, ItemsSource = rows };
            var builder = new SheetColumnBuilder(localization);
            // Six wide columns in a 600 px window: the table overflows horizontally.
            foreach (var key in new[] { "FinishRead.Col.FullName", "Col.B", "Col.C", "Col.D", "Col.E", "Col.F" })
                builder.Text(key, nameof(Row.Name), width: 220);
            table.Bands = builder.Bands;
            var window = new Window { Width = 600, Height = 320, Content = table };
            window.Show();
            return (window, table);
        });
        await app.SettleAsync();
        await app.SettleAsync();
        return shown;
    }

    private static ScrollViewer Scroll(SheetTable table, string field) =>
        (ScrollViewer)typeof(SheetTable).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(table)!;
}
