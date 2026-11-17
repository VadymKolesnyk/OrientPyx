using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.Presentation.Controls;
using OrientPyx.Presentation.ViewModels;
using OrientPyx.Presentation.ViewModels.Dialogs;
using OrientPyx.Presentation.ViewModels.Pages;
using OrientPyx.Tests.Support;
using Xunit.Abstractions;

namespace OrientPyx.Tests.Ui;

/// <summary>
/// «Масово редагувати виділені значення» (Shift+F6 / the cell menu), driven through real input on the headless
/// window: a range of several cells in one column opens the bulk-edit dialog fixed on that column's field, and the
/// value lands on the selected rows only — on the roster's day column, on that day's cells only.
/// </summary>
[Collection(AppCollection.Name)]
public sealed class BulkEditSelectionUiTests
{
    private readonly HeadlessApp _app;
    private readonly ITestOutputHelper _output;

    public BulkEditSelectionUiTests(HeadlessApp app, ITestOutputHelper output)
    {
        _app = app;
        _output = output;
    }

    private static IServiceProvider Core => TestEnvironment.Instance.Services;
    private static IEventStore Store => Core.GetRequiredService<IEventStore>();

    [Fact]
    public async Task ShiftF6EditsOnlyTheSelectedCellsOfTheColumn()
    {
        var c = await CompetitionBuilder.CreateAsync(Core, TestIds.Next("ui_bes"));
        await c.AddGroupAsync("Ч21", "31");
        for (var i = 1; i <= 4; i++)
            await c.AddRunnerAsync($"Бігун {i:00}", i.ToString(), "", "Ч21", configure: p => p.Coach = "Старий");
        await OpenCompetitionAsync(c.Summary.Identifier);
        var (participants, table) = await OpenRosterAsync(expectedRows: 4);

        bool IsCoach(SheetColumn col) => col.Key == "id:Participants.Col.Coach";
        await SelectRangeAsync(table, IsCoach, fromRow: 0, toRow: 2);

        await _app.OnUiAsync(() => _app.Window.KeyPress(Key.F6, RawInputModifiers.Shift, PhysicalKey.F6, null));
        var dialog = await DialogAsync();
        Assert.True(dialog.SelectionOnly);
        Assert.False(dialog.CanPickField);
        Assert.Equal("Coach", Assert.Single(dialog.Fields).Key);
        Assert.Equal(3, dialog.AffectedCount);
        await _app.ScreenshotAsync("bulk-selection-10-dialog");

        await _app.OnUiAsync(() =>
        {
            dialog.TextValue = "Новий";
            dialog.ConfirmCommand.Execute(null);
        });
        await _app.SettleAsync();

        var shown = await _app.OnUiAsync(() => table.VisibleItems.Cast<ParticipantRosterRowViewModel>()
            .Select(r => r.Coach).ToList());
        _output.WriteLine(string.Join(", ", shown));
        Assert.Equal(["Новий", "Новий", "Новий", "Старий"], shown);

        // The coach saves itself in the background: the store must end with the same values.
        var expected = await _app.OnUiAsync(() => table.VisibleItems.Cast<ParticipantRosterRowViewModel>()
            .ToDictionary(r => r.ParticipantId, r => r.Coach));
        await _app.WaitUntilAsync(() => Store.GetParticipantsAsync(c.Folder).GetAwaiter().GetResult()
            .All(p => p.Coach == expected[p.Id]), what: "coaches persisted");
        AssertNoUiErrors();
    }

    [Fact]
    public async Task RosterDayColumnSelectionChangesOnlyThatDay()
    {
        var c = await CompetitionBuilder.CreateAsync(Core, TestIds.Next("ui_besd"), dayCount: 2);
        await c.AddGroupAsync("Ч21", "31");
        for (var i = 1; i <= 4; i++)
            await c.AddRunnerAsync($"Бігун {i:00}", i.ToString(), "", "Ч21", start: CompetitionBuilder.At(10, i));
        await OpenCompetitionAsync(c.Summary.Identifier);
        var (participants, table) = await OpenRosterAsync(expectedRows: 4);

        // Expand «Старт» into one column per day.
        var block = participants.Blocks.Single(b => b.Field == RosterField.StartTimes);
        await _app.OnUiAsync(() => participants.ToggleBlockCommand.Execute(block));
        await _app.SettleAsync();
        bool IsDay2Start(SheetColumn col) => col.Kind == SheetCellKind.StartTime && col.DayIndex == 1;
        await _app.WaitUntilAsync(() => FindCell(table, 0, IsDay2Start) is not null, what: "day-2 start column built");

        await SelectRangeAsync(table, IsDay2Start, fromRow: 1, toRow: 3);
        await _app.OnUiAsync(() => _app.Window.KeyPress(Key.F6, RawInputModifiers.Shift, PhysicalKey.F6, null));
        var dialog = await DialogAsync();
        var field = Assert.Single(dialog.Fields);
        Assert.Equal("StartTime", field.Key);
        Assert.Contains("2", field.Label); // «Час старту — День 2»
        await _app.OnUiAsync(() =>
        {
            dialog.TextValue = "11:00:00";
            dialog.ConfirmCommand.Execute(null);
        });
        await _app.SettleAsync();

        var day1 = c.Day(1).Id;
        var day2 = c.Day(2).Id;
        var number = (await Store.GetParticipantsAsync(c.Folder)).ToDictionary(p => p.Id, p => p.Number);
        var order = await _app.OnUiAsync(() => table.VisibleItems.Cast<ParticipantRosterRowViewModel>()
            .Select(r => r.ParticipantId).ToList());

        await _app.WaitUntilAsync(() => Store.GetParticipantDaysAsync(c.Folder, day2).GetAwaiter().GetResult()
            .Count(l => l.StartTime == CompetitionBuilder.At(11, 0)) == 3, what: "three day-2 starts saved");
        var links2 = (await Store.GetParticipantDaysAsync(c.Folder, day2)).ToDictionary(l => l.ParticipantId);
        var links1 = (await Store.GetParticipantDaysAsync(c.Folder, day1)).ToDictionary(l => l.ParticipantId);
        for (var i = 0; i < order.Count; i++)
        {
            var id = order[i];
            var own = CompetitionBuilder.At(10, int.Parse(number[id]));
            _output.WriteLine($"{number[id]}: day1 {links1[id].StartTime} day2 {links2[id].StartTime}");
            Assert.Equal(own, links1[id].StartTime);
            Assert.Equal(i == 0 ? own : CompetitionBuilder.At(11, 0), links2[id].StartTime);
        }
        AssertNoUiErrors();
    }

    private async Task<BulkEditViewModel> DialogAsync()
    {
        await _app.WaitUntilAsync(() => _app.Vm.Dialogs.Current is BulkEditViewModel, what: "bulk-edit dialog shown");
        return (BulkEditViewModel)_app.Vm.Dialogs.Current!;
    }

    // Click the first cell, Shift+click the last: a range of several rows inside the one column.
    private async Task SelectRangeAsync(SheetTable table, Func<SheetColumn, bool> column, int fromRow, int toRow)
    {
        var window = _app.Window;
        var from = await _app.OnUiAsync(() => CellCenter(table, fromRow, column));
        await _app.OnUiAsync(() => window.MouseDown(from, MouseButton.Left));
        await _app.OnUiAsync(() => window.MouseUp(from, MouseButton.Left));
        await _app.SettleAsync();
        var to = await _app.OnUiAsync(() => CellCenter(table, toRow, column));
        await _app.OnUiAsync(() => window.MouseDown(to, MouseButton.Left, RawInputModifiers.Shift));
        await _app.OnUiAsync(() => window.MouseUp(to, MouseButton.Left, RawInputModifiers.Shift));
        await _app.SettleAsync();
        await _app.WaitUntilAsync(() => table.SelectedRowItems.Count == toRow - fromRow + 1, what: "range selected");
    }

    private async Task<(ParticipantsViewModel, SheetTable)> OpenRosterAsync(int expectedRows)
    {
        await _app.OnUiAsync(() => _app.Vm.OpenParticipantsCommand.ExecuteAsync(null));
        await _app.SettleAsync();
        var participants = Assert.IsType<ParticipantsViewModel>(Shell().SelectedPage);
        if (!participants.IsRosterMode)
        {
            await _app.OnUiAsync(() => participants.SelectedDay = participants.DayOptions.First(d => d.IsRoster));
            await _app.SettleAsync();
        }
        await _app.WaitUntilAsync(() => participants.IsRosterMode && participants.Roster.Count == expectedRows,
            what: "roster rows");
        var table = await _app.OnUiAsync(() =>
            _app.Window.GetVisualDescendants().OfType<SheetTable>().Single(t => t.Name == "RosterTable"));
        await _app.WaitUntilAsync(() => table.VisibleItems.Count == expectedRows, what: "roster table shows the rows");
        await _app.SettleAsync();
        await _app.SettleAsync();
        await _app.OnUiAsync(() => table.Focus());
        await _app.SettleAsync();
        return (participants, table);
    }

    // The window-space centre of a displayed row's cell in the matching column, scrolled into view first.
    private static Point CellCenter(SheetTable table, int rowIndex, Func<SheetColumn, bool> column)
    {
        var cell = FindCell(table, rowIndex, column) ?? throw new InvalidOperationException($"no realized cell for row {rowIndex}");
        cell.BringIntoView();
        table.UpdateLayout();
        var centre = new Point(cell.Bounds.Width / 2, cell.Bounds.Height / 2);
        return cell.TranslatePoint(centre, TopLevel.GetTopLevel(table)!) ?? throw new InvalidOperationException("cell not in the tree");
    }

    private static Control? FindCell(SheetTable table, int rowIndex, Func<SheetColumn, bool> column)
    {
        var row = table.VisibleItems[rowIndex];
        foreach (var d in table.GetVisualDescendants())
        {
            if (d.GetType().Name != "SheetCell" || d is not Control cell || !ReferenceEquals(cell.DataContext, row))
                continue;
            if (d.GetType().GetProperty("Column")!.GetValue(d) is SheetColumn col && column(col))
                return cell;
        }
        return null;
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
