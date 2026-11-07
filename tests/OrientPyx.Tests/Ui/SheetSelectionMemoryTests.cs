using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using OrientPyx.Presentation.Controls;
using OrientPyx.Presentation.ViewModels;
using OrientPyx.Presentation.ViewModels.Pages;
using OrientPyx.Tests.Support;
using Xunit.Abstractions;

namespace OrientPyx.Tests.Ui;

/// <summary>
/// Scrolling a table and walking it with the arrow keys must not keep memory. At a real competition the host grew
/// to ~6 GB during an hour of registration on «Мандатка»: the body rebuilds the rows it scrolls to, and every
/// discarded row stayed alive — the table held its lock binding (<c>SheetLock</c>) and the rental-chip registry held
/// its chip cells (<c>ChipHighlight</c>), ~1 MB a row. Both now hold nothing past the row's time in the tree.
/// </summary>
[Collection(AppCollection.Name)]
public sealed class SheetSelectionMemoryTests
{
    private const int Runners = 60;
    private const int Steps = 40;

    private readonly HeadlessApp _app;
    private readonly ITestOutputHelper _output;

    public SheetSelectionMemoryTests(HeadlessApp app, ITestOutputHelper output)
    {
        _app = app;
        _output = output;
    }

    private static IServiceProvider Core => TestEnvironment.Instance.Services;

    [Fact]
    public async Task ArrowKeysThroughTheRoster_DoNotKeepMemory()
    {
        var table = await OpenTableAsync(roster: true);

        var window = _app.Window;
        var first = await _app.OnUiAsync(() => CellCenter(table, 0));
        await _app.OnUiAsync(() => window.MouseDown(first, MouseButton.Left));
        await _app.OnUiAsync(() => window.MouseUp(first, MouseButton.Left));
        await _app.SettleAsync();

        async Task MoveAsync(int count, Key key)
        {
            for (var i = 0; i < count; i++)
            {
                await _app.OnUiAsync(() => window.KeyPress(key, RawInputModifiers.None,
                    key == Key.Down ? PhysicalKey.ArrowDown : PhysicalKey.ArrowUp, null));
                await _app.SettleAsync();
            }
        }

        await MoveAsync(5, Key.Down);
        await MoveAsync(5, Key.Up);
        var before = Managed();
        await MoveAsync(Steps / 2, Key.Down); // past the viewport: the table scrolls and builds new rows
        await MoveAsync(Steps / 2, Key.Up);
        AssertKept(before, "an arrow key on the roster");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ScrollingTheTable_DoesNotKeepMemory(bool roster)
    {
        var table = await OpenTableAsync(roster);
        var body = await _app.OnUiAsync(() => table.GetVisualDescendants().OfType<ListBox>().First());

        async Task ScrollAsync(int i)
        {
            await _app.OnUiAsync(() => body.ScrollIntoView(i % 2 == 0 ? Runners - 10 : 0));
            await _app.SettleAsync();
        }

        for (var i = 0; i < 6; i++)
            await ScrollAsync(i);
        var before = Managed();
        for (var i = 0; i < Steps; i++)
            await ScrollAsync(i);
        AssertKept(before, roster ? "a scroll of the roster" : "a scroll of the day grid");
    }

    private void AssertKept(long before, string what)
    {
        var perStep = (Managed() - before) / (double)Steps;
        _output.WriteLine($"{what}: {perStep / 1024.0:0} KB kept per step");
        Assert.True(perStep < 100 * 1024, $"{what} keeps {perStep / 1024.0:0} KB");
    }

    // A competition with chips (rental highlight) and the delete column (a lock-aware control) on every row.
    private async Task<SheetTable> OpenTableAsync(bool roster)
    {
        var c = await CompetitionBuilder.CreateAsync(Core, TestIds.Next(roster ? "ui_memr" : "ui_memd"), dayCount: 2);
        for (var g = 0; g < 5; g++)
            await c.AddGroupAsync($"Ж{12 + g * 2}", "31-32");
        for (var i = 1; i <= Runners; i++)
            await c.AddRunnerAsync($"Бігунка {i:00}", i.ToString(), (200000 + i).ToString(), $"Ж{12 + (i % 5) * 2}");
        await OpenCompetitionAsync(c.Summary.Identifier);

        await _app.OnUiAsync(() => _app.Vm.OpenParticipantsCommand.ExecuteAsync(null));
        await _app.SettleAsync();
        var page = Assert.IsType<ParticipantsViewModel>(Shell().SelectedPage);
        var wanted = await _app.OnUiAsync(() => page.DayOptions.First(d => d.IsRoster == roster));
        if (await _app.OnUiAsync(() => page.SelectedDay != wanted))
        {
            await _app.OnUiAsync(() => page.SelectedDay = wanted);
            await _app.SettleAsync();
        }
        await _app.WaitUntilAsync(() => (roster ? page.Roster.Count : page.Participants.Count) == Runners, what: "rows loaded");
        var name = roster ? "RosterTable" : "DayTable";
        var table = await _app.OnUiAsync(() => _app.Window.GetVisualDescendants().OfType<SheetTable>().Single(t => t.Name == name));
        await _app.WaitUntilAsync(() => table.VisibleItems.Count == Runners, what: "table rows");
        await _app.SettleAsync();
        return table;
    }

    private static long Managed()
    {
        for (var i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        return GC.GetTotalMemory(forceFullCollection: true);
    }

    private static Point CellCenter(SheetTable table, int rowIndex)
    {
        var row = table.VisibleItems[rowIndex];
        foreach (var d in table.GetVisualDescendants())
        {
            if (d.GetType().Name != "SheetCell" || d is not Control cell || !ReferenceEquals(cell.DataContext, row))
                continue;
            var column = d.GetType().GetProperty("Column")!.GetValue(d)!;
            if (column.GetType().GetProperty("ShowCount")!.GetValue(column) is true)
                return cell.TranslatePoint(new Point(cell.Bounds.Width / 2, cell.Bounds.Height / 2), TopLevel.GetTopLevel(table)!)
                       ?? throw new InvalidOperationException("cell not in the tree");
        }
        throw new InvalidOperationException($"no realized «Номер» cell for row {rowIndex}");
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
}
