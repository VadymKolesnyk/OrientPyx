using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Presentation.Controls;
using OrientPyx.Presentation.ViewModels;
using OrientPyx.Presentation.ViewModels.Pages;
using OrientPyx.Tests.Support;

namespace OrientPyx.Tests.Ui;

/// <summary>
/// A relay group «за номером етапу»: the group cell of a runner whose leg number has no course the results can use is red
/// with the reason as its tooltip — in the day table and in the roster (per day and collapsed) — and turns back once the
/// leg number gets a variation of the team's group.
/// </summary>
[Collection(AppCollection.Name)]
public sealed class RelayLegCourseCellUiTests
{
    private readonly HeadlessApp _app;

    public RelayLegCourseCellUiTests(HeadlessApp app) => _app = app;

    private static IServiceProvider Core => TestEnvironment.Instance.Services;
    private static IEventStore Store => Core.GetRequiredService<IEventStore>();

    // RowHighlight.FlagBrush — the age-window red.
    private static readonly Color Red = Color.FromRgb(0xF6, 0xCB, 0xC8);

    [Fact]
    public async Task GroupCellOfALegNumberWithoutACourseIsRed()
    {
        var c = await CompetitionBuilder.CreateAsync(Core, TestIds.Next("ui_reglegcourse"), dayCount: 2, discipline: DisciplineId.Relay);
        await c.AddControlsAsync(1, "31", "32");
        await c.AddGroupAsync("R", "31 32", s => { s.LegCount = 2; s.MassStartTime = new TimeSpan(10, 0, 0); });
        await c.AddGroupAsync("Q", "31 32", s => s.LegCount = 2);
        await c.SetCourseVariantsAsync("R", 1, ("A", "31 32"), ("B", "32 31"));
        await c.SetRelayCourseModeAsync("R", 1, RelayCourseMode.ByBib);
        await c.AddRunnerAsync("Етап 1", "", "8011", "R");
        await c.AddRunnerAsync("Етап 2", "", "8012", "R");
        await c.AddRelayTeamAsync("R", 801, "Сокіл", 1, ("Етап 1", 1), ("Етап 2", 2));
        var dayId = c.Day(1).Id;
        await Store.UpsertLegCourseAssignmentsAsync(c.Folder, dayId,
        [
            new LegCourseAssignment { EventDayId = dayId, TeamNumber = 801, Leg = 1, CourseKey = "A", GroupId = c.Groups["R"].Id },
            new LegCourseAssignment { EventDayId = dayId, TeamNumber = 801, Leg = 2, CourseKey = "B", GroupId = c.Groups["Q"].Id },
        ]);
        await OpenCompetitionAsync(c.Summary.Identifier);

        await _app.OnUiAsync(() => _app.Vm.OpenParticipantsCommand.ExecuteAsync(null));
        await _app.SettleAsync();
        var page = Assert.IsType<ParticipantsViewModel>(Shell().SelectedPage);
        if (!page.IsRosterMode)
        {
            await _app.OnUiAsync(() => page.SelectedDay = page.DayOptions.First(d => d.IsRoster));
            await _app.SettleAsync();
        }
        await _app.WaitUntilAsync(() => page.IsRosterMode && page.Roster.Count == 2, what: "two roster rows");
        var roster = await _app.OnUiAsync(() =>
            _app.Window.GetVisualDescendants().OfType<SheetTable>().Single(t => t.Name == "RosterTable"));
        await _app.WaitUntilAsync(() => roster.VisibleItems.Count == 2, what: "roster table shows the rows");
        await _app.SettleAsync();
        ParticipantRosterRowViewModel Row(string name) => page.Roster.Single(r => r.FullName == name);

        // Collapsed «Групи»: red for the leg given another group's variation, naming the day and the leg number.
        var collapsedOk = await _app.OnUiAsync(() => Cell(roster, Row("Етап 1"), "block:Groups"));
        var collapsedBad = await _app.OnUiAsync(() => Cell(roster, Row("Етап 2"), "block:Groups"));
        Assert.NotEqual(Red, collapsedOk.Background);
        Assert.Equal(Red, collapsedBad.Background);
        Assert.Contains("801.2", collapsedBad.Tooltip);
        Assert.Contains("«Q»", collapsedBad.Tooltip);

        // Per day: only day 1 (the «за номером етапу» day) is red.
        await _app.OnUiAsync(() => page.ToggleBlockCommand.Execute(page.Blocks.Single(bl => bl.Field == RosterField.Groups)));
        await _app.SettleAsync();
        await _app.SettleAsync();
        await _app.ScreenshotAsync("relay-leg-course-10-roster");
        var day1 = await _app.OnUiAsync(() => Cell(roster, Row("Етап 2"), "block:Groups:day1"));
        var day2 = await _app.OnUiAsync(() => Cell(roster, Row("Етап 2"), "block:Groups:day2"));
        Assert.Equal(Red, day1.Background);
        Assert.Equal("R", day1.Text);
        Assert.NotEqual(Red, day2.Background);
        await _app.OnUiAsync(() => page.ToggleBlockCommand.Execute(page.Blocks.Single(bl => bl.Field == RosterField.Groups)));
        await _app.SettleAsync();

        // The day table of day 1.
        await _app.OnUiAsync(() => page.SelectedDay = page.DayOptions.First(d => d.Number == 1));
        await _app.SettleAsync();
        await _app.WaitUntilAsync(() => page.IsDayMode && page.Participants.Count == 2, what: "two day-1 rows");
        var dayTable = await _app.OnUiAsync(() =>
            _app.Window.GetVisualDescendants().OfType<SheetTable>().Single(t => t.Name == "DayTable"));
        await _app.WaitUntilAsync(() => dayTable.VisibleItems.Count == 2, what: "day table shows the rows");
        await _app.SettleAsync();
        await _app.ScreenshotAsync("relay-leg-course-20-day");
        ParticipantDayRowViewModel DayRow(string name) => page.Participants.Single(r => r.FullName == name);
        var okCell = await _app.OnUiAsync(() => Cell(dayTable, DayRow("Етап 1"), "id:Participants.Col.Group"));
        var badCell = await _app.OnUiAsync(() => Cell(dayTable, DayRow("Етап 2"), "id:Participants.Col.Group"));
        Assert.NotEqual(Red, okCell.Background);
        Assert.Equal(Red, badCell.Background);
        Assert.Contains("801.2", badCell.Tooltip);

        // The variation moved to the team's group (as on «Варіанти за номерами»): back on «Учасники» the cell is no longer red.
        await Store.UpsertLegCourseAssignmentsAsync(c.Folder, dayId,
            [new LegCourseAssignment { EventDayId = dayId, TeamNumber = 801, Leg = 2, CourseKey = "B", GroupId = c.Groups["R"].Id }]);
        await _app.OnUiAsync(() => _app.Vm.OpenParticipantsCommand.ExecuteAsync(null));
        await _app.SettleAsync();
        await _app.WaitUntilAsync(() => !DayRow("Етап 2").Relay.HasCourseIssue, what: "the leg number has a course");
        await _app.SettleAsync();
        badCell = await _app.OnUiAsync(() => Cell(dayTable, DayRow("Етап 2"), "id:Participants.Col.Group"));
        Assert.NotEqual(Red, badCell.Background);
        Assert.True(string.IsNullOrEmpty(badCell.Tooltip));
        AssertNoUiErrors();
    }

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
