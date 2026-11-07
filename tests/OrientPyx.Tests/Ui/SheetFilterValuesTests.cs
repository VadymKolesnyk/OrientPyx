using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using OrientPyx.Presentation.Controls;
using OrientPyx.Tests.Support;

namespace OrientPyx.Tests.Ui;

/// <summary>
/// The «by values» picker of a <see cref="SheetTable"/> column lists only the values of the rows the OTHER filters
/// leave (filtered by representative, the name list holds only that representative's runners), still lists the
/// values the column's own filter hides, and unchecking one keeps the rows of the values it didn't list — clearing
/// the other filter brings everyone back except the unchecked runner. A right-click on a header opens that filter
/// editor straight away; its «Інші дії» button opens the column menu (hide column, …).
/// </summary>
[Collection(AppCollection.Name)]
public sealed class SheetFilterValuesTests(HeadlessApp app)
{
    public sealed class Row(string name, string rep)
    {
        public string Name { get; } = name;
        public string Rep { get; } = rep;
    }

    [Fact]
    public async Task ValuesPickerListsOnlyRowsPassingOtherFilters()
    {
        var rows = new ObservableCollection<Row>
        {
            new("Андрієнко", "Коваль"),
            new("Бондар", "Коваль"),
            new("Гнатюк", "Коваль"),
            new("Демченко", "Мельник"),
            new("Ємець", "Мельник"),
        };
        var (window, table, name, rep) = await app.OnUiAsync(() =>
        {
            var localization = app.Vm.Localization;
            var sheet = new SheetTable { Localization = localization, ItemsSource = rows };
            sheet.Bands = new SheetColumnBuilder(localization)
                .Text("FinishRead.Col.FullName", nameof(Row.Name), width: 160)
                .Text("Participants.Col.Representative", nameof(Row.Rep), width: 160)
                .Bands;
            var columns = sheet.Bands!.SelectMany(b => b.Columns).ToList();
            var w = new Window { Width = 500, Height = 300, Content = sheet };
            w.Show();
            return (w, sheet, columns[0], columns[1]);
        });
        try
        {
            await app.SettleAsync();
            await app.OnUiAsync(() =>
            {
                // No other filter: every value is listed.
                Assert.Equal(5, table.DistinctValues(name, passingOtherFilters: true).Count);

                table.SetColumnFilter(rep.Key, new SheetFilter
                {
                    ColumnKey = rep.Key, Mode = SheetFilterMode.Values, AllowedValues = ["Коваль"]
                });
                Assert.Equal(new[] { "Андрієнко", "Бондар", "Гнатюк" }, table.DistinctValues(name, passingOtherFilters: true));
                // The representative column's own filter is ignored for its own list.
                Assert.Equal(new[] { "Коваль", "Мельник" }, table.DistinctValues(rep, passingOtherFilters: true));

                // Uncheck «Бондар» in the (narrowed) name list.
                var nameFilter = new SheetFilter { ColumnKey = name.Key, Mode = SheetFilterMode.Values };
                var listed = table.DistinctValues(name, passingOtherFilters: true);
                nameFilter.ApplyPickedValues(listed, listed.Where(v => v != "Бондар"),
                    new HashSet<string>(table.DistinctValues(name)));
                table.SetColumnFilter(name.Key, nameFilter);

                Assert.Equal(new[] { "Андрієнко", "Гнатюк" }, table.VisibleItems.Cast<Row>().Select(r => r.Name));
                // The unchecked value is still listed (unchecked) — the name filter doesn't narrow its own list.
                Assert.Contains("Бондар", table.DistinctValues(name, passingOtherFilters: true));

                // Clearing the representative filter shows everyone but the unchecked runner.
                table.ClearColumnFilter(rep.Key);
                Assert.Equal(new[] { "Андрієнко", "Гнатюк", "Демченко", "Ємець" },
                    table.VisibleItems.Cast<Row>().Select(r => r.Name));

                // Re-checking everything listed drops the filter altogether.
                listed = table.DistinctValues(name, passingOtherFilters: true);
                nameFilter.ApplyPickedValues(listed, listed, new HashSet<string>(table.DistinctValues(name)));
                Assert.Null(nameFilter.AllowedValues);
            });
        }
        finally
        {
            await app.OnUiAsync(window.Close);
        }
    }

    [Fact]
    public async Task HeaderRightClickOpensFilterAndMoreActionsOpensColumnMenu()
    {
        var rows = new ObservableCollection<Row> { new("Андрієнко", "Коваль"), new("Демченко", "Мельник") };
        // A flyout's popup lives outside the window's trees (its own top level), so collect every popup as it opens.
        var popups = new List<Popup>();
        using var watch = await app.OnUiAsync(() =>
            Popup.IsOpenProperty.Changed.AddClassHandler<Popup>((p, _) => { if (p.IsOpen && !popups.Contains(p)) popups.Add(p); }));
        var (window, header) = await app.OnUiAsync(() =>
        {
            var localization = app.Vm.Localization;
            var sheet = new SheetTable { Localization = localization, ItemsSource = rows };
            sheet.Bands = new SheetColumnBuilder(localization)
                .Text("FinishRead.Col.FullName", nameof(Row.Name), width: 160)
                .Text("Participants.Col.Representative", nameof(Row.Rep), width: 160)
                .Bands;
            var w = new Window { Width = 500, Height = 300, Content = sheet };
            w.Show();
            return (w, localization.Get("FinishRead.Col.FullName"));
        });
        try
        {
            await app.SettleAsync();
            var point = await app.OnUiAsync(() =>
            {
                var label = window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == header);
                return label.TranslatePoint(new Point(label.Bounds.Width / 2, label.Bounds.Height / 2), window)!.Value;
            });
            await app.OnUiAsync(() => window.MouseDown(point, MouseButton.Right));
            await app.OnUiAsync(() => window.MouseUp(point, MouseButton.Right));
            await app.SettleAsync();

            // The filter editor is open (not the old menu): it has «Застосувати» and «Інші дії».
            var loc = app.Vm.Localization;
            var filterTexts = await app.OnUiAsync(() => OpenPopupTexts(popups));
            Assert.Contains(loc.Get("Sheet.Filter.Apply"), filterTexts);
            Assert.DoesNotContain(loc.Get("Sheet.Columns.Hide"), filterTexts);

            // The values search is layout-tolerant like the table's: «Fylh» is «Андр» typed in the Latin layout.
            await app.OnUiAsync(() => OpenPopups(popups).SelectMany(p => p.GetLogicalDescendants()).OfType<TextBox>()
                .First(t => t.PlaceholderText == loc.Get("Sheet.Filter.Search")).Text = "Fylh");
            await app.SettleAsync();
            var shownValues = await app.OnUiAsync(() => OpenPopups(popups).SelectMany(p => p.GetLogicalDescendants())
                .OfType<CheckBox>().Where(c => c.IsVisible).Select(c => c.Tag as string).ToList());
            Assert.Equal(new[] { "Андрієнко" }, shownValues);

            await app.OnUiAsync(() =>
            {
                var more = OpenPopups(popups).SelectMany(p => p.GetLogicalDescendants()).OfType<Button>()
                    .Single(b => b.GetLogicalDescendants().OfType<TextBlock>().Any(t => t.Text == loc.Get("Sheet.Filter.MoreActions")));
                more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            await app.SettleAsync();

            // Now only the column menu is open.
            var menuTexts = await app.OnUiAsync(() => OpenPopupTexts(popups));
            Assert.Contains(loc.Get("Sheet.Columns.Hide"), menuTexts);
            Assert.DoesNotContain(loc.Get("Sheet.Filter.Apply"), menuTexts);
        }
        finally
        {
            await app.OnUiAsync(window.Close);
        }
    }

    private static List<Popup> OpenPopups(List<Popup> seen) => seen.Where(p => p.IsOpen).ToList();

    private static List<string> OpenPopupTexts(List<Popup> seen)
        => OpenPopups(seen).SelectMany(p => p.GetLogicalDescendants()).OfType<TextBlock>()
            .Select(t => t.Text ?? string.Empty).ToList();
}
