using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using OrientPyx.Presentation.Controls;
using OrientPyx.Tests.Support;

namespace OrientPyx.Tests.Ui;

/// <summary>
/// Selecting a <see cref="SheetTable"/> cell must not change the row's height. The focus outline used to be the
/// cell's own border (0,0,1,0 → 1.5 all round), which added 3 px; a cell whose content already fills the row (a
/// CheckBox or a small button, MinHeight 32) handed that on to the row, so it jumped taller on every selection.
/// The outline is now an overlay of fixed thickness that only changes colour.
/// </summary>
[Collection(AppCollection.Name)]
public sealed class SheetFocusRowHeightTests(HeadlessApp app)
{
    public sealed class Row(string name, bool flag)
    {
        public string Name { get; } = name;
        public bool Flag { get; set; } = flag;
    }

    [Fact]
    public async Task FocusingACheckBoxOrButtonCell_KeepsTheRowHeight()
    {
        var rows = new ObservableCollection<Row> { new("Перший", true), new("Другий", false) };
        var (window, table) = await app.OnUiAsync(() =>
        {
            var localization = app.Vm.Localization;
            var sheet = new SheetTable { Localization = localization, ItemsSource = rows };
            sheet.Bands = new SheetColumnBuilder(localization)
                .Text("FinishRead.Col.FullName", nameof(Row.Name), width: 160)
                .Check("Participants.Col.Number", nameof(Row.Flag))
                .DeleteAction(_ => { }, "Common.Delete")
                .Bands;
            var w = new Window { Width = 500, Height = 260, Content = sheet };
            w.Show();
            return (w, sheet);
        });
        try
        {
            await app.SettleAsync();
            var cells = await app.OnUiAsync(() => FirstRowCells(window));
            Assert.Equal(3, cells.Count);
            var item = await app.OnUiAsync(() => cells[0].FindAncestorOfType<ListBoxItem>()!);
            var initial = await app.OnUiAsync(() => item.Bounds.Height);

            foreach (var (cell, what) in new[] { (cells[1], "checkbox"), (cells[2], "button"), (cells[0], "text") })
            {
                await app.OnUiAsync(() => cell.Focus());
                await app.SettleAsync();
                await app.SettleAsync();

                var (focused, height, edge) = await app.OnUiAsync(() =>
                {
                    var bounds = BoundsIn(window, cell);
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    using var frame = window.CaptureRenderedFrame()
                                      ?? throw new InvalidOperationException("The headless window produced no frame.");
                    using var buffer = frame.Lock();
                    return (cell.IsFocused, item.Bounds.Height, Sample(buffer, bounds.Center.X, bounds.Bottom - 1));
                });

                Assert.True(focused, $"the {what} cell did not take focus");
                Assert.True(Math.Abs(height - initial) < 0.01,
                    $"focusing the {what} cell changed the row height {initial} → {height}");
                // The outline is still drawn: the cell's bottom edge is the accent colour.
                var (r, g, b) = edge;
                Assert.True(b > 200 && r < 170 && g < 120, $"the focused {what} cell has no accent outline: {edge}");
            }
        }
        finally
        {
            await app.OnUiAsync(window.Close);
        }
    }

    // The first row's cells, left to right.
    private static List<ContentControl> FirstRowCells(Window window)
    {
        var cells = window.GetVisualDescendants().OfType<ContentControl>()
            .Where(c => c.GetType().Name == "SheetCell")
            .Select(c => (Cell: c, Bounds: BoundsIn(window, c)))
            .ToList();
        var top = cells.Min(c => c.Bounds.Y);
        return cells.Where(c => Math.Abs(c.Bounds.Y - top) < 0.5)
            .OrderBy(c => c.Bounds.X).Select(c => c.Cell).ToList();
    }

    private static Rect BoundsIn(Visual root, Visual cell)
    {
        var origin = cell.TranslatePoint(new Point(0, 0), root) ?? default;
        return new Rect(origin, cell.Bounds.Size);
    }

    // Reads one pixel of a Bgra8888 / Rgba8888 frame (opaque window, so premultiplication is moot).
    private static (int R, int G, int B) Sample(Avalonia.Platform.ILockedFramebuffer buffer, double x, double y)
    {
        var px = (int)Math.Floor(x);
        var py = (int)Math.Floor(y);
        var value = Marshal.ReadInt32(buffer.Address, py * buffer.RowBytes + px * 4);
        int c0 = value & 0xFF, c1 = (value >> 8) & 0xFF, c2 = (value >> 16) & 0xFF;
        return buffer.Format == Avalonia.Platform.PixelFormat.Rgba8888 ? (c0, c1, c2) : (c2, c1, c0);
    }
}
