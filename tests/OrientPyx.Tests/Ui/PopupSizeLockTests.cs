using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using OrientPyx.Presentation.Controls;
using OrientPyx.Tests.Support;

namespace OrientPyx.Tests.Ui;

/// <summary>
/// A popup whose content changes while open keeps its opening size (on macOS a resized popup window kept its old
/// frame stretched into the new size — the filter editor looked squashed): the column filter editor while its value
/// search shortens the list and while it switches modes, and a searchable combo's dropdown while typing.
/// </summary>
[Collection(AppCollection.Name)]
public sealed class PopupSizeLockTests(HeadlessApp app)
{
    public sealed class Row(string name)
    {
        public string Name { get; } = name;
    }

    [Fact]
    public async Task FilterEditorKeepsItsSizeWhileSearchingAndSwitchingModes()
    {
        var rows = new ObservableCollection<Row>(new[]
        {
            "Андрієнко Олександра", "Бондар", "Гнатюк", "Демченко", "Ємець", "Жук", "Зінченко", "Іваненко", "Коваль", "Лисенко"
        }.Select(n => new Row(n)));
        var popups = new List<Popup>();
        using var watch = await app.OnUiAsync(() =>
            Popup.IsOpenProperty.Changed.AddClassHandler<Popup>((p, _) => { if (p.IsOpen && !popups.Contains(p)) popups.Add(p); }));
        var loc = app.Vm.Localization;
        var header = loc.Get("FinishRead.Col.FullName");
        var window = await app.OnUiAsync(() =>
        {
            var sheet = new SheetTable { Localization = loc, ItemsSource = rows };
            sheet.Bands = new SheetColumnBuilder(loc).Text("FinishRead.Col.FullName", nameof(Row.Name), width: 160).Bands;
            var w = new Window { Width = 500, Height = 600, Content = sheet };
            w.Show();
            return w;
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

            var editor = await app.OnUiAsync(() => popups.Single(p => p.IsOpen));
            var opened = await app.OnUiAsync(() => editor.Child!.Bounds.Size);

            // «Fylh» (= «Андр» in the Latin layout) leaves one value of ten.
            await app.OnUiAsync(() => editor.GetLogicalDescendants().OfType<TextBox>()
                .First(t => t.PlaceholderText == loc.Get("Sheet.Filter.Search")).Text = "Fylh");
            await app.SettleAsync();
            Assert.Equal(1, await app.OnUiAsync(() => editor.GetLogicalDescendants().OfType<CheckBox>().Count(c => c.IsVisible)));
            Assert.Equal(opened, await app.OnUiAsync(() => editor.Child!.Bounds.Size));

            await app.OnUiAsync(() => editor.GetLogicalDescendants().OfType<RadioButton>()
                .First(r => Equals(r.Content, loc.Get("Sheet.Filter.Mode.Condition"))).IsChecked = true);
            await app.SettleAsync();
            Assert.Equal(opened, await app.OnUiAsync(() => editor.Child!.Bounds.Size));
        }
        finally
        {
            await app.OnUiAsync(window.Close);
        }
    }

    [Fact]
    public async Task SearchableComboDropDownKeepsItsSizeWhileTyping()
    {
        var items = Enumerable.Range(1, 12).Select(i => $"Група {i}").ToList();
        var (window, combo) = await app.OnUiAsync(() =>
        {
            var c = new SearchableComboBox { ItemsSource = items, Width = 160 };
            var w = new Window { Width = 400, Height = 600, Content = c };
            w.Show();
            return (w, c);
        });
        try
        {
            await app.SettleAsync();
            await app.OnUiAsync(() => combo.IsDropDownOpen = true);
            await app.SettleAsync();
            var popup = await app.OnUiAsync(() => combo.GetVisualDescendants().OfType<Popup>().Single());
            var opened = await app.OnUiAsync(() => popup.Child!.Bounds.Size);

            await app.OnUiAsync(() => popup.Child!.GetLogicalDescendants().OfType<TextBox>().First().Text = "12");
            await app.SettleAsync();
            Assert.Equal(1, await app.OnUiAsync(() => combo.ItemCount));
            Assert.Equal(opened, await app.OnUiAsync(() => popup.Child!.Bounds.Size));

            // Closed, the pin is dropped so the next open sizes to its content.
            await app.OnUiAsync(() => combo.IsDropDownOpen = false);
            await app.SettleAsync();
            Assert.Equal(0, await app.OnUiAsync(() => ((Control)popup.Child!).MinHeight));
        }
        finally
        {
            await app.OnUiAsync(window.Close);
        }
    }
}
