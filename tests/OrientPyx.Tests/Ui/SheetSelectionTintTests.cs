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
/// A selected <see cref="SheetTable"/> row must not hide a cell tint (e.g. the roster's green «Мандатку пройдено»
/// cell): the selection wash is translucent so the tint blends through, Fluent's own strong selected-row fill is
/// dropped, and the selected row gets an accent frame so a fully tinted row still reads as selected.
/// </summary>
[Collection(AppCollection.Name)]
public sealed class SheetSelectionTintTests(HeadlessApp app)
{
    public sealed class Row(string number, string name, bool checkedIn)
    {
        public string Number { get; } = number;
        public string Name { get; } = name;
        public bool CheckedIn { get; } = checkedIn;
    }

    [Fact]
    public async Task SelectedRowKeepsTheCellTintAndIsFramed()
    {
        var rows = new ObservableCollection<Row>
        {
            new("101", "Перший", true),
            new("102", "Другий", false),
            new("103", "Третій", true),
        };
        var (window, table) = await app.OnUiAsync(() =>
        {
            var localization = app.Vm.Localization;
            var sheet = new SheetTable { Localization = localization, ItemsSource = rows };
            sheet.Bands = new SheetColumnBuilder(localization)
                .Text("Participants.Col.Number", nameof(Row.Number), width: 120)
                .CellTint(nameof(Row.CheckedIn), DayColumnBuilder.CheckedInBrush)
                .Text("FinishRead.Col.FullName", nameof(Row.Name), width: 160)
                .Bands;
            var w = new Window { Width = 500, Height = 260, Content = sheet };
            w.Show();
            return (w, sheet);
        });
        try
        {
            await app.SettleAsync();
            await app.OnUiAsync(() => table.SelectedItem = rows[0]);
            await app.SettleAsync();
            await app.SettleAsync();

            var pixels = await app.OnUiAsync(() =>
            {
                var cells = window.GetVisualDescendants().OfType<ContentControl>()
                    .Where(c => c.GetType().Name == "SheetCell")
                    .Select(c => (Cell: c, Bounds: BoundsIn(window, c)))
                    .OrderBy(c => c.Bounds.Y).ThenBy(c => c.Bounds.X)
                    .ToList();
                var selectedTinted = cells[0].Bounds;   // row 1 «№» (checked in, selected)
                var selectedPlain = cells[1].Bounds;    // row 1 name (selected)
                var plainTinted = cells[4].Bounds;      // row 3 «№» (checked in, not selected)
                var unselected = cells[3].Bounds;       // row 2 name (not selected)

                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                using var frame = window.CaptureRenderedFrame()
                                  ?? throw new InvalidOperationException("The headless window produced no frame.");
                frame.Save(Path.Combine(TestEnvironment.ResultsDir, "sheet-selection-tint.png"));
                using var buffer = frame.Lock();
                return (
                    SelectedTinted: Sample(buffer, selectedTinted.Right - 6, selectedTinted.Center.Y),
                    SelectedPlain: Sample(buffer, selectedPlain.Right - 6, selectedPlain.Center.Y),
                    PlainTinted: Sample(buffer, plainTinted.Right - 6, plainTinted.Center.Y),
                    SelectedTop: Sample(buffer, selectedPlain.Center.X, selectedPlain.Top),
                    UnselectedTop: Sample(buffer, unselected.Center.X, unselected.Top));
            });

            // The checked-in green still dominates under the selection.
            var (r, g, b) = pixels.SelectedTinted;
            Assert.True(g > r + 25 && g > b, $"selected tinted cell lost its green: {pixels.SelectedTinted}");
            // An untinted selected cell is the light lavender wash, not Fluent's strong accent fill.
            (r, g, b) = pixels.SelectedPlain;
            Assert.True(r > 215 && g > 210 && b > 240, $"selected cell is not the light wash: {pixels.SelectedPlain}");
            // The selected row is framed in the accent colour; an unselected row is not.
            (r, g, b) = pixels.SelectedTop;
            Assert.True(b > 200 && r < 170 && g < 120, $"selected row has no accent frame: {pixels.SelectedTop}");
            (r, _, _) = pixels.UnselectedTop;
            Assert.True(r > 200, $"unselected row looks framed: {pixels.UnselectedTop}");
            // The tint itself is unchanged on an unselected row.
            (r, g, _) = pixels.PlainTinted;
            Assert.True(g > r + 40, $"unselected tinted cell is not green: {pixels.PlainTinted}");
        }
        finally
        {
            await app.OnUiAsync(window.Close);
        }
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
