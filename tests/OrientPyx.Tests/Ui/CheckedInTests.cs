using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.Presentation.Controls;

using OrientPyx.Presentation.ViewModels;
using OrientPyx.Presentation.ViewModels.Pages;
using OrientPyx.Tests.Support;
using Xunit.Abstractions;

namespace OrientPyx.Tests.Ui;

/// <summary>
/// «Мандатку пройдено» on the roster, driven through real input on the headless window: a Shift+click range
/// over several rows + F8 marks them (green «Номер» cell, flag persisted), Ctrl+A + F8 marks the rest, and a
/// second F8 over an all-marked selection clears it.
/// </summary>
[Collection(AppCollection.Name)]
public sealed class CheckedInTests
{
    private readonly HeadlessApp _app;
    private readonly ITestOutputHelper _output;

    public CheckedInTests(HeadlessApp app, ITestOutputHelper output)
    {
        _app = app;
        _output = output;
    }

    private static IServiceProvider Core => TestEnvironment.Instance.Services;
    private static IEventStore Store => Core.GetRequiredService<IEventStore>();

    [Fact]
    public async Task F8TogglesCheckedInOverTheSelectedRowsAndCtrlASelectsAll()
    {
        var c = await CompetitionBuilder.CreateAsync(Core, TestIds.Next("ui_chk"));
        await c.AddGroupAsync("Ч21", "31");
        for (var i = 1; i <= 4; i++)
            await c.AddRunnerAsync($"Бігун {i:00}", i.ToString(), "", "Ч21");
        await OpenCompetitionAsync(c.Summary.Identifier);

        await _app.OnUiAsync(() => _app.Vm.OpenParticipantsCommand.ExecuteAsync(null));
        await _app.SettleAsync();
        var participants = Assert.IsType<ParticipantsViewModel>(Shell().SelectedPage);
        if (!participants.IsRosterMode)
        {
            await _app.OnUiAsync(() => participants.SelectedDay = participants.DayOptions.First(d => d.IsRoster));
            await _app.SettleAsync();
        }
        await _app.WaitUntilAsync(() => participants.IsRosterMode && participants.Roster.Count == 4, what: "four roster rows");
        var table = await _app.OnUiAsync(() =>
            _app.Window.GetVisualDescendants().OfType<SheetTable>().Single(t => t.Name == "RosterTable"));
        await _app.WaitUntilAsync(() => table.VisibleItems.Count == 4, what: "roster table shows the rows");
        await _app.SettleAsync();
        await _app.SettleAsync();

        var window = _app.Window;
        await _app.OnUiAsync(() => table.Focus());
        await _app.SettleAsync();
        var first = await _app.OnUiAsync(() => CellCenter(table, rowIndex: 0));
        var third = await _app.OnUiAsync(() => CellCenter(table, rowIndex: 2));

        // Click row 1, Shift+click row 3 → a three-row range; F8 marks all three.
        await _app.OnUiAsync(() => window.MouseDown(first, MouseButton.Left));
        await _app.OnUiAsync(() => window.MouseUp(first, MouseButton.Left));
        await _app.SettleAsync();
        await _app.OnUiAsync(() => window.MouseDown(third, MouseButton.Left, RawInputModifiers.Shift));
        await _app.OnUiAsync(() => window.MouseUp(third, MouseButton.Left, RawInputModifiers.Shift));
        await _app.SettleAsync();
        await _app.WaitUntilAsync(() => table.SelectedRowItems.Count == 3, what: "three-row range selected");

        await _app.OnUiAsync(() => window.KeyPress(Key.F8, RawInputModifiers.None, PhysicalKey.F8, null));
        await _app.SettleAsync();
        await _app.WaitUntilAsync(() => participants.Roster.Count(r => r.CheckedIn) == 3, what: "three rows marked");
        var rows = await _app.OnUiAsync(() => participants.Roster.Select(r => (r.Number, r.CheckedIn)).ToList());
        _output.WriteLine(string.Join(", ", rows.Select(r => $"{r.Number}:{r.CheckedIn}")));
        Assert.Equal([true, true, true, false], rows.OrderBy(r => r.Number).Select(r => r.CheckedIn));
        await _app.ScreenshotAsync("checkin-10-three-marked");

        // The marked row's «Номер» cell is tinted; the unmarked one is not.
        var tintedBrush = await _app.OnUiAsync(() => CellBackground(table, rowIndex: 0));
        var plainBrush = await _app.OnUiAsync(() => CellBackground(table, rowIndex: 3));
        Assert.Same(DayColumnBuilder.CheckedInBrush, tintedBrush);
        Assert.NotSame(DayColumnBuilder.CheckedInBrush, plainBrush);
        Assert.Contains("3", await _app.OnUiAsync(() => participants.StatusInfo ?? string.Empty));

        // The write runs in the background: the store must show the same three flags.
        await _app.WaitUntilAsync(() =>
            Store.GetParticipantsAsync(c.Folder).GetAwaiter().GetResult().Count(p => p.CheckedIn) == 3,
            what: "three flags persisted");

        // A plain click on the unmarked row collapses the range, so the green tint is visible on its own.
        var fourth = await _app.OnUiAsync(() => CellCenter(table, rowIndex: 3));
        await _app.OnUiAsync(() => window.MouseDown(fourth, MouseButton.Left));
        await _app.OnUiAsync(() => window.MouseUp(fourth, MouseButton.Left));
        await _app.SettleAsync();
        await _app.SettleAsync();
        await _app.WaitUntilAsync(() => table.SelectedRowItems.Count == 1, what: "range collapsed to the clicked row");
        await _app.ScreenshotAsync("checkin-11-collapsed");

        // Ctrl+A selects every row; one of them is unmarked, so F8 marks all four.
        await _app.OnUiAsync(() => window.KeyPress(Key.A, RawInputModifiers.Control, PhysicalKey.A, "a"));
        await _app.SettleAsync();
        await _app.WaitUntilAsync(() => table.SelectedRowItems.Count == 4, what: "Ctrl+A selected every row");
        await _app.OnUiAsync(() => window.KeyPress(Key.F8, RawInputModifiers.None, PhysicalKey.F8, null));
        await _app.SettleAsync();
        await _app.WaitUntilAsync(() => participants.Roster.All(r => r.CheckedIn), what: "every row marked");

        // All marked: F8 again clears every selected row.
        await _app.OnUiAsync(() => window.KeyPress(Key.F8, RawInputModifiers.None, PhysicalKey.F8, null));
        await _app.SettleAsync();
        await _app.WaitUntilAsync(() => participants.Roster.All(r => !r.CheckedIn), what: "every row cleared");
        await _app.WaitUntilAsync(() =>
            Store.GetParticipantsAsync(c.Folder).GetAwaiter().GetResult().All(p => !p.CheckedIn),
            what: "flags cleared in the store");
        AssertNoUiErrors();
    }

    [Fact]
    public async Task NumberColumnFiltersByCheckedInStatus()
    {
        var c = await CompetitionBuilder.CreateAsync(Core, TestIds.Next("ui_chkf"));
        await c.AddGroupAsync("Ч21", "31");
        for (var i = 1; i <= 4; i++)
            await c.AddRunnerAsync($"Бігун {i:00}", i.ToString(), "", "Ч21");
        await OpenCompetitionAsync(c.Summary.Identifier);

        // Two of the four are through registration.
        var marked = (await Store.GetParticipantsAsync(c.Folder)).OrderBy(p => p.Number).Take(2).Select(p => p.Id).ToList();
        await Store.SetParticipantsCheckedInAsync(c.Folder, marked, true);

        await _app.OnUiAsync(() => _app.Vm.OpenParticipantsCommand.ExecuteAsync(null));
        await _app.SettleAsync();
        var participants = Assert.IsType<ParticipantsViewModel>(Shell().SelectedPage);
        if (!participants.IsRosterMode)
        {
            await _app.OnUiAsync(() => participants.SelectedDay = participants.DayOptions.First(d => d.IsRoster));
            await _app.SettleAsync();
        }
        await _app.WaitUntilAsync(() => participants.IsRosterMode && participants.Roster.Count == 4, what: "four roster rows");
        var table = await _app.OnUiAsync(() =>
            _app.Window.GetVisualDescendants().OfType<SheetTable>().Single(t => t.Name == "RosterTable"));
        await _app.WaitUntilAsync(() => table.VisibleItems.Count == 4, what: "roster table shows the rows");

        const string numberKey = "id:Participants.Col.Number";

        // «За станом» → only «Мандатку пройдено»: the two marked rows.
        await _app.OnUiAsync(() => table.SetColumnFilter(numberKey, new SheetFilter
        {
            ColumnKey = numberKey, Mode = SheetFilterMode.Status, AllowedStatuses = [CheckedInStatus.Yes]
        }));
        await _app.SettleAsync();
        await _app.WaitUntilAsync(() => table.VisibleItems.Count == 2, what: "only the checked-in rows");
        Assert.All(table.VisibleItems, r => Assert.True(((ParticipantRosterRowViewModel)r!).CheckedIn));
        await _app.ScreenshotAsync("checkin-20-filter-checked");

        // Only «не пройдено»: the other two.
        await _app.OnUiAsync(() => table.SetColumnFilter(numberKey, new SheetFilter
        {
            ColumnKey = numberKey, Mode = SheetFilterMode.Status, AllowedStatuses = [CheckedInStatus.No]
        }));
        await _app.SettleAsync();
        await _app.WaitUntilAsync(() => table.VisibleItems.Count == 2, what: "only the not-checked-in rows");
        Assert.All(table.VisibleItems, r => Assert.False(((ParticipantRosterRowViewModel)r!).CheckedIn));

        // The by-values mode of the same column still filters on the visible numbers.
        await _app.OnUiAsync(() => table.SetColumnFilter(numberKey, new SheetFilter
        {
            ColumnKey = numberKey, Mode = SheetFilterMode.Values, AllowedValues = ["3"]
        }));
        await _app.SettleAsync();
        await _app.WaitUntilAsync(() => table.VisibleItems.Count == 1, what: "number 3 alone");

        await _app.OnUiAsync(() => table.ClearAllFilters());
        await _app.SettleAsync();
        await _app.WaitUntilAsync(() => table.VisibleItems.Count == 4, what: "filter cleared");
        AssertNoUiErrors();
    }

    // The window-space centre of the «Номер» cell (the column that shows the row count) of the given displayed row.
    private static Point CellCenter(SheetTable table, int rowIndex)
    {
        var cell = NumberCell(table, rowIndex);
        var centre = new Point(cell.Bounds.Width / 2, cell.Bounds.Height / 2);
        return cell.TranslatePoint(centre, TopLevel.GetTopLevel(table)!) ?? throw new InvalidOperationException("cell not in the tree");
    }

    private static IBrush? CellBackground(SheetTable table, int rowIndex)
        => (IBrush?)NumberCell(table, rowIndex).GetType().GetProperty("Background")!.GetValue(NumberCell(table, rowIndex));

    private static Control NumberCell(SheetTable table, int rowIndex)
    {
        var row = table.VisibleItems[rowIndex];
        foreach (var d in table.GetVisualDescendants())
        {
            if (d.GetType().Name != "SheetCell" || d is not Control cell || !ReferenceEquals(cell.DataContext, row))
                continue;
            var column = d.GetType().GetProperty("Column")!.GetValue(d)!;
            if (column.GetType().GetProperty("ShowCount")!.GetValue(column) is true)
                return cell;
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

    private void AssertNoUiErrors()
    {
        if (_app.UiErrors.Count == 0)
            return;
        var text = string.Join(Environment.NewLine, _app.UiErrors.Select(e => e.ToString()));
        _app.UiErrors.Clear();
        Assert.Fail("The UI thread reported an exception:" + Environment.NewLine + text);
    }
}
