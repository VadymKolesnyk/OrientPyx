using System.Collections.ObjectModel;
using Avalonia.Controls;
using OrientPyx.Presentation.Controls;
using OrientPyx.Tests.Support;

namespace OrientPyx.Tests.Ui;

/// <summary>
/// The table search box skips a column marked <see cref="SheetColumnBuilder.NotSearchable"/> (the finish-read log's
/// running «Id»), and the window's toast stack (the «заберіть прокатний чіп» reminder) keeps at most five notices,
/// each closable on its own.
/// </summary>
[Collection(AppCollection.Name)]
public sealed class SheetSearchAndToastTests(HeadlessApp app)
{
    public sealed class Row(int order, string chip)
    {
        public int Order { get; } = order;
        public string Chip { get; } = chip;
    }

    [Fact]
    public async Task SearchIgnoresANotSearchableColumn()
    {
        var rows = new ObservableCollection<Row> { new(1, "7005"), new(15, "8001"), new(2, "1500") };
        var (window, table) = await app.OnUiAsync(() =>
        {
            var localization = app.Vm.Localization;
            var sheet = new SheetTable { Localization = localization, ItemsSource = rows };
            sheet.Bands = new SheetColumnBuilder(localization)
                .Text("FinishRead.Col.Id", nameof(Row.Order)).NotSearchable()
                .Text("FinishRead.Col.Chip", nameof(Row.Chip))
                .Bands;
            var w = new Window { Width = 500, Height = 300, Content = sheet };
            w.Show();
            return (w, sheet);
        });
        try
        {
            await app.SettleAsync();
            await app.OnUiAsync(() => table.GlobalSearch = "15");
            await app.WaitUntilAsync(() => table.VisibleItems.Count < rows.Count, what: "search applied");

            // Only the chip «1500» matches — the row whose «Id» is 15 does not.
            var shown = await app.OnUiAsync(() => table.VisibleItems.Cast<Row>().Select(r => r.Chip).ToList());
            Assert.Equal(["1500"], shown);
        }
        finally
        {
            await app.OnUiAsync(window.Close);
        }
    }

    [Fact]
    public async Task ToastStackKeepsTheNewestFive_AndClosesOneOnItsCross()
    {
        var toasts = app.Vm.Toasts;
        await app.OnUiAsync(() =>
        {
            for (var i = 1; i <= 7; i++)
                toasts.Show($"Тост {i}", $"Чіп 700{i}");
        });
        try
        {
            var titles = await app.OnUiAsync(() => toasts.Items.Select(t => t.Title).ToList());
            Assert.Equal(["Тост 3", "Тост 4", "Тост 5", "Тост 6", "Тост 7"], titles);

            await app.OnUiAsync(() => toasts.Items[0].DismissCommand.Execute(null));
            Assert.Equal(4, await app.OnUiAsync(() => toasts.Items.Count));
        }
        finally
        {
            // Leave no toast over the other tests' screenshots.
            await app.OnUiAsync(() => toasts.Items.Clear());
        }
    }
}
