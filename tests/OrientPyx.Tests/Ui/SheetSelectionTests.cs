using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OrientPyx.Presentation.Controls;
using OrientPyx.Presentation.ViewModels;
using OrientPyx.Presentation.ViewModels.Pages;
using OrientPyx.Tests.Support;
using Xunit.Abstractions;

namespace OrientPyx.Tests.Ui;

/// <summary>
/// Multi-cell range selection on the day grid driven through real pointer/keyboard input on the headless
/// window: a press-drag that scrolls the anchor row out of view must keep the keyboard on the table (Ctrl+C
/// still copies the range), the wheel must scroll while the button is held, and the status bar must sum
/// the selected fee cells.
/// </summary>
[Collection(AppCollection.Name)]
public sealed class SheetSelectionTests
{
    private readonly HeadlessApp _app;
    private readonly ITestOutputHelper _output;

    public SheetSelectionTests(HeadlessApp app, ITestOutputHelper output)
    {
        _app = app;
        _output = output;
    }

    private static IServiceProvider Core => TestEnvironment.Instance.Services;

    [Fact]
    public async Task DragSelectionThatScrollsTheAnchorAwayStillCopies()
    {
        var c = await CompetitionBuilder.CreateAsync(Core, TestIds.Next("ui_sel"));
        await c.AddGroupAsync("Ч21", "31");
        for (var i = 1; i <= 60; i++)
            await c.AddRunnerAsync($"Бігун {i:00}", i.ToString(), "", "Ч21");
        await OpenCompetitionAsync(c.Summary.Identifier);

        await _app.OnUiAsync(() => _app.Vm.OpenParticipantsCommand.ExecuteAsync(null));
        await _app.SettleAsync();
        var participants = Assert.IsType<ParticipantsViewModel>(Shell().SelectedPage);
        if (participants.IsRosterMode)
        {
            await _app.OnUiAsync(() => participants.SelectedDay = participants.DayOptions.First(d => !d.IsRoster));
            await _app.SettleAsync();
        }
        await _app.WaitUntilAsync(() => participants.IsDayMode && participants.Participants.Count == 60, what: "60 day rows");

        var (width, height) = await _app.OnUiAsync(() => (_app.Window.Width, _app.Window.Height));
        await _app.OnUiAsync(() => { _app.Window.Width = 1100; _app.Window.Height = 520; });
        await _app.SettleAsync();
        await _app.SettleAsync();

        var table = await _app.OnUiAsync(() =>
            _app.Window.GetVisualDescendants().OfType<SheetTable>().Single(t => t.Name == "DayTable"));
        await _app.WaitUntilAsync(() => table.VisibleItems.Count == 60, what: "day table shows the rows");
        await _app.SettleAsync();

        try
        {
            // Press on the first row's «Номер» cell, drag onto the fourth row (a real drag: the table captures the
            // pointer), then scroll the body far enough that the anchor row is virtualized away.
            var window = _app.Window;
            var first = await _app.OnUiAsync(() => CellCenter(table, rowIndex: 0, "number"));
            var fourth = await _app.OnUiAsync(() => CellCenter(table, rowIndex: 3, "number"));
            _output.WriteLine($"first {first}, fourth {fourth}");
            await _app.OnUiAsync(() => window.MouseDown(first, MouseButton.Left));
            await _app.SettleAsync();
            await _app.OnUiAsync(() => window.MouseMove(fourth, RawInputModifiers.LeftMouseButton));
            await _app.SettleAsync();

            var scroll = await _app.OnUiAsync(() => BodyScroll(table));
            await _app.OnUiAsync(() => scroll.Offset = new Vector(0, 800));
            await _app.SettleAsync();
            await _app.SettleAsync();
            await _app.OnUiAsync(() => window.MouseMove(fourth + new Point(0, 1), RawInputModifiers.LeftMouseButton));
            await _app.SettleAsync();
            await _app.OnUiAsync(() => window.MouseUp(fourth + new Point(0, 1), MouseButton.Left, RawInputModifiers.LeftMouseButton));
            await _app.SettleAsync();

            var focused = await _app.OnUiAsync(() => TopLevel.GetTopLevel(table)?.FocusManager?.GetFocusedElement());
            var offsetNow = await _app.OnUiAsync(() => scroll.Offset);
            _output.WriteLine($"focused after drag+scroll: {focused?.GetType().Name ?? "null"}, offset {offsetNow}");
            await _app.ScreenshotAsync("sel-10-after-drag-scroll");

            await _app.OnUiAsync(() => window.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, "c"));
            await _app.SettleAsync();
            var text = await await _app.OnUiAsync(() => TopLevel.GetTopLevel(table)!.Clipboard!.TryGetTextAsync());
            _output.WriteLine($"clipboard: [{text?.Replace("\n", "|")}]");
            Assert.False(string.IsNullOrEmpty(text), "Ctrl+C copied nothing after the anchor row scrolled away");
            Assert.StartsWith("1\n2\n", text);
        }
        finally
        {
            await _app.OnUiAsync(() => { _app.Window.Width = width; _app.Window.Height = height; });
            await _app.SettleAsync();
        }
        AssertNoUiErrors();
    }

    [Fact]
    public async Task WheelScrollsWhileDragSelecting()
    {
        var c = await CompetitionBuilder.CreateAsync(Core, TestIds.Next("ui_whl"));
        await c.AddGroupAsync("Ч21", "31");
        for (var i = 1; i <= 60; i++)
            await c.AddRunnerAsync($"Бігун {i:00}", i.ToString(), "", "Ч21");
        await OpenCompetitionAsync(c.Summary.Identifier);

        await _app.OnUiAsync(() => _app.Vm.OpenParticipantsCommand.ExecuteAsync(null));
        await _app.SettleAsync();
        var participants = Assert.IsType<ParticipantsViewModel>(Shell().SelectedPage);
        if (participants.IsRosterMode)
        {
            await _app.OnUiAsync(() => participants.SelectedDay = participants.DayOptions.First(d => !d.IsRoster));
            await _app.SettleAsync();
        }
        await _app.WaitUntilAsync(() => participants.IsDayMode && participants.Participants.Count == 60, what: "60 day rows");

        var (width, height) = await _app.OnUiAsync(() => (_app.Window.Width, _app.Window.Height));
        await _app.OnUiAsync(() => { _app.Window.Width = 1100; _app.Window.Height = 520; });
        await _app.SettleAsync();
        await _app.SettleAsync();

        var table = await _app.OnUiAsync(() =>
            _app.Window.GetVisualDescendants().OfType<SheetTable>().Single(t => t.Name == "DayTable"));
        await _app.WaitUntilAsync(() => table.VisibleItems.Count == 60, what: "day table shows the rows");
        await _app.SettleAsync();

        try
        {
            var window = _app.Window;
            var first = await _app.OnUiAsync(() => CellCenter(table, rowIndex: 0, "number"));
            var fourth = await _app.OnUiAsync(() => CellCenter(table, rowIndex: 3, "number"));
            await _app.OnUiAsync(() => window.MouseDown(first, MouseButton.Left));
            await _app.SettleAsync();
            await _app.OnUiAsync(() => window.MouseMove(fourth, RawInputModifiers.LeftMouseButton));
            await _app.SettleAsync();

            var scroll = await _app.OnUiAsync(() => BodyScroll(table));
            var before = await _app.OnUiAsync(() => scroll.Offset);
            await _app.OnUiAsync(() => window.MouseWheel(fourth, new Vector(0, -3), RawInputModifiers.LeftMouseButton));
            await _app.SettleAsync();
            await _app.SettleAsync();
            var after = await _app.OnUiAsync(() => scroll.Offset);
            _output.WriteLine($"offset before {before}, after wheel {after}");
            Assert.True(after.Y > before.Y, "the wheel did not scroll the body while drag-selecting");

            await _app.OnUiAsync(() => window.MouseUp(fourth, MouseButton.Left, RawInputModifiers.LeftMouseButton));
            await _app.SettleAsync();
        }
        finally
        {
            await _app.OnUiAsync(() => { _app.Window.Width = width; _app.Window.Height = height; });
            await _app.SettleAsync();
        }
        AssertNoUiErrors();
    }

    [Fact]
    public async Task CtrlShiftCCopiesTheRangeUnderItsHeaders()
    {
        var c = await CompetitionBuilder.CreateAsync(Core, TestIds.Next("ui_cph"));
        await c.AddGroupAsync("Ч21", "31");
        for (var i = 1; i <= 5; i++)
            await c.AddRunnerAsync($"Бігун {i:00}", i.ToString(), "", "Ч21");
        await OpenCompetitionAsync(c.Summary.Identifier);

        await _app.OnUiAsync(() => _app.Vm.OpenParticipantsCommand.ExecuteAsync(null));
        await _app.SettleAsync();
        var participants = Assert.IsType<ParticipantsViewModel>(Shell().SelectedPage);
        if (participants.IsRosterMode)
        {
            await _app.OnUiAsync(() => participants.SelectedDay = participants.DayOptions.First(d => !d.IsRoster));
            await _app.SettleAsync();
        }
        await _app.WaitUntilAsync(() => participants.IsDayMode && participants.Participants.Count == 5, what: "5 day rows");

        var table = await _app.OnUiAsync(() =>
            _app.Window.GetVisualDescendants().OfType<SheetTable>().Single(t => t.Name == "DayTable"));
        await _app.WaitUntilAsync(() => table.VisibleItems.Count == 5, what: "day table shows the rows");
        await _app.SettleAsync();

        // Press-drag over the first three «Номер» cells, then Ctrl+Shift+C: the header line comes first,
        // the three numbers follow — the same as Ctrl+C with the column header on top.
        var window = _app.Window;
        var first = await _app.OnUiAsync(() => CellCenter(table, rowIndex: 0, "number"));
        var third = await _app.OnUiAsync(() => CellCenter(table, rowIndex: 2, "number"));
        await _app.OnUiAsync(() => window.MouseDown(first, MouseButton.Left));
        await _app.SettleAsync();
        await _app.OnUiAsync(() => window.MouseMove(third, RawInputModifiers.LeftMouseButton));
        await _app.SettleAsync();
        await _app.OnUiAsync(() => window.MouseUp(third, MouseButton.Left, RawInputModifiers.LeftMouseButton));
        await _app.SettleAsync();

        await _app.OnUiAsync(() => window.KeyPress(Key.C, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.C, "C"));
        await _app.SettleAsync();
        var text = await await _app.OnUiAsync(() => TopLevel.GetTopLevel(table)!.Clipboard!.TryGetTextAsync());
        _output.WriteLine($"clipboard: [{text?.Replace("\n", "|")}]");
        Assert.False(string.IsNullOrEmpty(text), "Ctrl+Shift+C copied nothing");
        var lines = text!.Split('\n');
        Assert.Equal(4, lines.Length);
        Assert.False(int.TryParse(lines[0], out _), $"the first line should be the column header, got «{lines[0]}»");
        Assert.Equal(new[] { "1", "2", "3" }, lines.Skip(1));
        AssertNoUiErrors();
    }

    [Fact]
    public async Task SelectingPaymentCellsShowsTheirSumInTheStatusBar()
    {
        var c = await CompetitionBuilder.CreateAsync(Core, TestIds.Next("ui_sum"));
        await c.AddGroupAsync("Ч21", "31");
        await c.AddRunnerAsync("Платник Перший", "1", "", "Ч21", configure: p => p.Payment = "100");
        await c.AddRunnerAsync("Платник Другий", "2", "", "Ч21", configure: p => p.Payment = "50.5");
        await c.AddRunnerAsync("Платник Третій", "3", "", "Ч21", configure: p => p.Payment = "x");
        await c.AddRunnerAsync("Платник Четвертий", "4", "", "Ч21", configure: p => p.Payment = "1000");
        await OpenCompetitionAsync(c.Summary.Identifier);

        await _app.OnUiAsync(() => _app.Vm.OpenParticipantsCommand.ExecuteAsync(null));
        await _app.SettleAsync();
        var participants = Assert.IsType<ParticipantsViewModel>(Shell().SelectedPage);
        if (participants.IsRosterMode)
        {
            await _app.OnUiAsync(() => participants.SelectedDay = participants.DayOptions.First(d => !d.IsRoster));
            await _app.SettleAsync();
        }
        await _app.WaitUntilAsync(() => participants.IsDayMode && participants.Participants.Count == 4, what: "four day rows");
        var table = await _app.OnUiAsync(() =>
            _app.Window.GetVisualDescendants().OfType<SheetTable>().Single(t => t.Name == "DayTable"));
        await _app.WaitUntilAsync(() => table.VisibleItems.Count == 4, what: "day table shows the rows");
        await _app.SettleAsync();
        await _app.SettleAsync();

        // «Оплата» sits far to the right: bring it into the viewport first.
        var bodyScroll = await _app.OnUiAsync(() => BodyScroll(table));
        await _app.OnUiAsync(() => bodyScroll.Offset = new Vector(ScrollToCellX(table, "payment"), 0));
        await _app.SettleAsync();
        await _app.SettleAsync();

        var window = _app.Window;
        var first = await _app.OnUiAsync(() => CellCenter(table, rowIndex: 0, "payment"));
        var third = await _app.OnUiAsync(() => CellCenter(table, rowIndex: 2, "payment"));
        await _app.OnUiAsync(() => window.MouseDown(first, MouseButton.Left));
        await _app.SettleAsync();
        await _app.OnUiAsync(() => window.MouseMove(third, RawInputModifiers.LeftMouseButton));
        await _app.SettleAsync();
        await _app.OnUiAsync(() => window.MouseUp(third, MouseButton.Left, RawInputModifiers.LeftMouseButton));
        await _app.SettleAsync();

        var text = await _app.OnUiAsync(() => table.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Name == "PART_StatusSelection").Text);
        _output.WriteLine($"selection sum: [{text}]");
        await _app.ScreenshotAsync("sel-20-payment-sum");
        Assert.StartsWith("Сума виділених:", text);
        Assert.Contains("= 150.5", text);   // 100 + 50.5; the "x" cell is not a number
        Assert.Contains("(клітинок: 2)", text);

        // A plain click collapses the range: the sum disappears.
        await _app.OnUiAsync(() => window.MouseDown(first, MouseButton.Left));
        await _app.OnUiAsync(() => window.MouseUp(first, MouseButton.Left));
        await _app.SettleAsync();
        var hidden = await _app.OnUiAsync(() => !table.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Name == "PART_StatusSelection").IsVisible);
        Assert.True(hidden, "the selection sum is still shown after the range collapsed");
        AssertNoUiErrors();
    }

    // The window-space centre of the cell at (row index in the displayed view, column key) — the cell must be realized.
    private static Point CellCenter(SheetTable table, int rowIndex, string columnKey)
    {
        var row = table.VisibleItems[rowIndex];
        foreach (var d in table.GetVisualDescendants())
        {
            if (d.GetType().Name != "SheetCell" || d is not Control cell || !ReferenceEquals(cell.DataContext, row))
                continue;
            var column = d.GetType().GetProperty("Column")!.GetValue(d)!;
            // "number" = the «Номер» column (the one that shows the row count); anything else = an exact key.
            var matches = columnKey switch
            {
                "number" => column.GetType().GetProperty("ShowCount")!.GetValue(column) is true,
                "payment" => (string?)column.GetType().GetProperty("SummaryPath")!.GetValue(column) == "Payment",
                _ => string.Equals((string?)column.GetType().GetProperty("Key")!.GetValue(column), columnKey, StringComparison.Ordinal),
            };
            if (!matches)
                continue;
            var centre = new Point(cell.Bounds.Width / 2, cell.Bounds.Height / 2);
            return cell.TranslatePoint(centre, TopLevel.GetTopLevel(table)!) ?? throw new InvalidOperationException("cell not in the tree");
        }
        throw new InvalidOperationException($"no realized cell for row {rowIndex} / column {columnKey}");
    }

    // A horizontal body offset that puts the given column's cell (row 0) inside the viewport.
    private static double ScrollToCellX(SheetTable table, string columnKey)
    {
        var scroll = BodyScroll(table);
        var centre = CellCenter(table, 0, columnKey);
        var viewportLeft = scroll.TranslatePoint(new Point(0, 0), TopLevel.GetTopLevel(table)!)!.Value.X;
        var target = scroll.Offset.X + (centre.X - viewportLeft) - scroll.Viewport.Width / 2;
        return Math.Clamp(target, 0, Math.Max(0, scroll.Extent.Width - scroll.Viewport.Width));
    }

    private static ScrollViewer BodyScroll(SheetTable table)
    {
        var field = typeof(SheetTable).GetField("_bodyScroll", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (ScrollViewer)field.GetValue(table)!;
    }

    private async Task OpenCompetitionAsync(string identifier)
    {
        await _app.SettleAsync();
        if (_app.Vm.IsEventSelected)
        {
            await _app.OnUiAsync(() => _app.Vm.ChangeEventCommand.ExecuteAsync(null));
            await _app.SettleAsync();
        }
        else if (_app.Vm.CurrentView is CreateEventViewModel create)
        {
            await _app.OnUiAsync(() => create.CancelCommand.Execute(null));
            await _app.SettleAsync();
        }
        await _app.WaitUntilAsync(() => _app.Vm.CurrentView is EventSelectionViewModel, what: "picker shown");
        var selection = (EventSelectionViewModel)_app.Vm.CurrentView;
        if (!selection.Events.Any(e => e.Identifier == identifier))
        {
            var items = await selection.FetchEventsAsync();
            await _app.OnUiAsync(() => selection.Populate(items));
        }
        await _app.WaitUntilAsync(() => selection.Events.Any(e => e.Identifier == identifier), what: $"{identifier} listed");
        await _app.OnUiAsync(async () =>
        {
            selection.SelectedEvent = selection.Events.Single(e => e.Identifier == identifier);
            await selection.OpenCommand.ExecuteAsync(null);
        });
        await _app.WaitUntilAsync(() => _app.Vm.IsEventSelected, what: $"{identifier} opened");
        await _app.SettleAsync();
    }

    private ShellViewModel Shell()
    {
        var field = typeof(MainWindowViewModel).GetField("_shell", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (ShellViewModel)field.GetValue(_app.Vm)!;
    }

    private void AssertNoUiErrors()
    {
        if (_app.UiErrors.Count == 0)
            return;
        var text = string.Join(Environment.NewLine, _app.UiErrors.Select(e => e.ToString()));
        _app.UiErrors.Clear();
        Assert.Fail("The UI thread reported an exception:" + Environment.NewLine + text);
    }
}
