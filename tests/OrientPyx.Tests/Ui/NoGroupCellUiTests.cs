using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.Presentation.Controls;
using OrientPyx.Presentation.ViewModels;
using OrientPyx.Presentation.ViewModels.Pages;
using OrientPyx.Tests.Support;

namespace OrientPyx.Tests.Ui;

/// <summary>
/// A participant who runs a day with no group on it (a UOF «3Е» with no relay group, a group taken off the day) reads
/// amber «без групи» in the group cell — never «не бере участі», which means "not on this day" — on the roster
/// (collapsed and per day) and in the day table; picking «не бере участі» there takes them off the day.
/// </summary>
[Collection(AppCollection.Name)]
public sealed class NoGroupCellUiTests
{
    private const string NoGroup = "без групи";
    private const string NotOnDay = "не бере участі";

    private readonly HeadlessApp _app;

    public NoGroupCellUiTests(HeadlessApp app) => _app = app;

    private static IServiceProvider Core => TestEnvironment.Instance.Services;
    private static IEventStore Store => Core.GetRequiredService<IEventStore>();

    [Fact]
    public async Task MemberWithoutGroupReadsAmberNoGroupAndCanLeaveTheDay()
    {
        var c = await CompetitionBuilder.CreateAsync(Core, TestIds.Next("ui_nogroup"), dayCount: 2);
        await c.AddGroupAsync("Ч21", "31");
        var day2 = c.Day(2).Id;
        await c.AddRunnerAsync("Бігун 01", "1", "", "Ч21", days: [1]);                       // not on day 2
        var b = await c.AddRunnerAsync("Бігун 02", "2", "", "Ч21",
            configureDay: l => { if (l.EventDayId == day2) l.GroupId = null; });             // day 2 with no group
        await c.AddRunnerAsync("Бігун 03", "3", "", groupName: null, days: [2]);             // day 2 only, no group
        await OpenCompetitionAsync(c.Summary.Identifier);

        await _app.OnUiAsync(() => _app.Vm.OpenParticipantsCommand.ExecuteAsync(null));
        await _app.SettleAsync();
        var page = Assert.IsType<ParticipantsViewModel>(Shell().SelectedPage);
        if (!page.IsRosterMode)
        {
            await _app.OnUiAsync(() => page.SelectedDay = page.DayOptions.First(d => d.IsRoster));
            await _app.SettleAsync();
        }
        await _app.WaitUntilAsync(() => page.IsRosterMode && page.Roster.Count == 3, what: "three roster rows");
        var roster = await _app.OnUiAsync(() =>
            _app.Window.GetVisualDescendants().OfType<SheetTable>().Single(t => t.Name == "RosterTable"));
        await _app.WaitUntilAsync(() => roster.VisibleItems.Count == 3, what: "roster table shows the rows");
        await _app.SettleAsync();
        ParticipantRosterRowViewModel Row(string name) => page.Roster.Single(r => r.FullName == name);

        // Collapsed «Групи»: amber wherever a day has no group; the all-no-group runner reads «без групи».
        var collapsedA = await _app.OnUiAsync(() => Cell(roster, Row("Бігун 01"), "block:Groups"));
        var collapsedB = await _app.OnUiAsync(() => Cell(roster, Row("Бігун 02"), "block:Groups"));
        var collapsedC = await _app.OnUiAsync(() => Cell(roster, Row("Бігун 03"), "block:Groups"));
        Assert.NotEqual(Amber, collapsedA.Background);
        Assert.Equal(Amber, collapsedB.Background);
        Assert.Contains("2", collapsedB.Tooltip);                                            // names the day
        Assert.Equal(Amber, collapsedC.Background);
        Assert.Equal(NoGroup, collapsedC.Text);

        // Per day: day 2 of the runner who is not on it stays «не бере участі»; the member with no group is amber.
        await _app.OnUiAsync(() => page.ToggleBlockCommand.Execute(page.Blocks.Single(bl => bl.Field == RosterField.Groups)));
        await _app.SettleAsync();
        await _app.SettleAsync();
        await _app.ScreenshotAsync("no-group-10-roster");
        var notOnDay = await _app.OnUiAsync(() => Cell(roster, Row("Бігун 01"), "block:Groups:day2"));
        var noGroup = await _app.OnUiAsync(() => Cell(roster, Row("Бігун 02"), "block:Groups:day2"));
        var ranDay1 = await _app.OnUiAsync(() => Cell(roster, Row("Бігун 02"), "block:Groups:day1"));
        Assert.Equal(NotOnDay, notOnDay.Text);
        Assert.NotEqual(Amber, notOnDay.Background);
        Assert.Equal(NoGroup, noGroup.Text);
        Assert.Equal(Amber, noGroup.Background);
        Assert.False(string.IsNullOrEmpty(noGroup.Tooltip));
        Assert.Equal("Ч21", ranDay1.Text);
        Assert.NotEqual(Amber, ranDay1.Background);
        Assert.Null(await _app.OnUiAsync(() => Row("Бігун 02").Days[1].ComboGroup));      // the combo shows no choice

        // The day table of day 2: the same runners, «без групи» amber.
        await _app.OnUiAsync(() => page.SelectedDay = page.DayOptions.First(d => d.Number == 2));
        await _app.SettleAsync();
        await _app.WaitUntilAsync(() => page.IsDayMode && page.Participants.Count == 2, what: "two day-2 rows");
        var dayTable = await _app.OnUiAsync(() =>
            _app.Window.GetVisualDescendants().OfType<SheetTable>().Single(t => t.Name == "DayTable"));
        await _app.WaitUntilAsync(() => dayTable.VisibleItems.Count == 2, what: "day table shows the rows");
        await _app.SettleAsync();
        await _app.ScreenshotAsync("no-group-20-day");
        var dayRow = await _app.OnUiAsync(() => page.Participants.Single(r => r.FullName == "Бігун 03"));
        var dayCell = await _app.OnUiAsync(() => Cell(dayTable, dayRow, "id:Participants.Col.Group"));
        Assert.Equal(NoGroup, dayCell.Text);
        Assert.Equal(Amber, dayCell.Background);
        Assert.Null(await _app.OnUiAsync(() => dayRow.ComboGroup));

        // Saving another field of that row keeps the stored "no group" — the day's first group (the list has no
        // "(none)" option) used to be written then.
        await _app.OnUiAsync(() => dayRow.Coach = "Тренер");
        var c3 = c.Runners["Бігун 03"].Id;
        await WaitForAsync(async () =>
            (await Store.GetParticipantsAsync(c.Folder)).Single(p => p.Id == c3).Coach == "Тренер", "the row saved");
        Assert.Null((await Store.GetParticipantDaysAsync(c.Folder, day2)).Single(l => l.ParticipantId == c3).GroupId);

        // Picking «не бере участі» on a member with no group takes them off the day (they run day 1 too, so no
        // last-day question).
        await _app.OnUiAsync(() => page.SelectedDay = page.DayOptions.First(d => d.IsRoster));
        await _app.SettleAsync();
        await _app.WaitUntilAsync(() => page.IsRosterMode && page.Roster.Count == 3, what: "roster again");
        await _app.OnUiAsync(() =>
        {
            var cell = Row("Бігун 02").Days[1];
            cell.ComboGroup = cell.GroupOptions[0];
        });
        await WaitForAsync(async () =>
            (await Store.GetParticipantDaysAsync(c.Folder, day2)).All(l => l.ParticipantId != b.Id), "left day 2");
        await _app.WaitUntilAsync(() => !Row("Бігун 02").Days[1].IsMember, what: "the cell is a non-member");
        Assert.Equal(NotOnDay, await _app.OnUiAsync(() => Row("Бігун 02").Days[1].GroupLabel));
        Assert.Single(await Store.GetParticipantDaysAsync(c.Folder, c.Day(1).Id), l => l.ParticipantId == b.Id);

        // The page is shared by the whole run: fold «Групи» back as the next test expects it.
        await _app.OnUiAsync(() => page.ToggleBlockCommand.Execute(page.Blocks.Single(bl => bl.Field == RosterField.Groups)));
        await _app.SettleAsync();
        AssertNoUiErrors();
    }

    // DayColumnBuilder.NoGroupBrush — the «Кількість карт» «(без групи)» amber.
    private static readonly Color Amber = Color.FromArgb(0x55, 0xF5, 0x9E, 0x0B);

    // The resting text, the cell's background colour (null = none) and tooltip of a row's column.
    private static (string? Text, Color? Background, string? Tooltip) Cell(SheetTable table, object row, string columnKey)
    {
        foreach (var d in table.GetVisualDescendants())
        {
            if (d.GetType().Name != "SheetCell" || d is not TemplatedControl cell || !ReferenceEquals(cell.DataContext, row))
                continue;
            var column = d.GetType().GetProperty("Column")!.GetValue(d)!;
            if (column.GetType().GetProperty("Key")!.GetValue(column) as string != columnKey)
                continue;
            var label = cell.GetVisualDescendants().OfType<TextBlock>().First(t => t.IsEffectivelyVisible);
            var background = cell.Background is ISolidColorBrush solid ? solid.Color : (Color?)null;
            return (label.Text, background, ToolTip.GetTip(cell) as string);
        }
        throw new InvalidOperationException($"no realized {columnKey} cell");
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition, string what)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (!await condition())
        {
            if (DateTime.UtcNow > until)
                throw new TimeoutException($"timed out waiting for: {what}");
            await Task.Delay(50);
        }
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
