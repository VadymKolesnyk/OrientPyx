using System.Reflection;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.Presentation.Controls;
using OrientPyx.Presentation.ViewModels;
using OrientPyx.Presentation.ViewModels.Pages;
using OrientPyx.Tests.Support;

namespace OrientPyx.Tests.Ui;

/// <summary>
/// «Змагання → Естафета → Варіанти за номерами» on the real page: the numbers with a variation above (a variation of
/// another group than the team's flagged), the legs without one below; a variation picked in the lower table is saved,
/// and F8 over a flagged row gives the number to the team's group.
/// </summary>
[Collection(AppCollection.Name)]
public sealed class NumberVariantsUiTests
{
    private readonly HeadlessApp _app;

    public NumberVariantsUiTests(HeadlessApp app) => _app = app;

    private static IServiceProvider Core => TestEnvironment.Instance.Services;
    private static IEventStore Store => Core.GetRequiredService<IEventStore>();

    [Fact]
    public async Task NumberVariantsPageEditsTheVariationsAndFlagsAnotherGroup()
    {
        var c = await CompetitionBuilder.CreateAsync(Core, TestIds.Next("ui_numvar"), discipline: DisciplineId.Relay);
        await c.AddControlsAsync(1, "31", "32");
        foreach (var group in new[] { "R", "Q" })
        {
            await c.AddGroupAsync(group, "31 32", s => s.LegCount = 2);
            await c.SetRelayCourseModeAsync(group, 1, RelayCourseMode.ByBib);
        }
        await c.SetCourseVariantsAsync("R", 1, ("A", "31 32"), ("B", "32 31"));
        await c.SetCourseVariantsAsync("Q", 1, ("X", "31 32"));
        foreach (var name in new[] { "R1", "R2", "S1", "S2" })
            await c.AddRunnerAsync(name, "", "", "R");
        await c.AddRelayTeamAsync("R", 101, "Азимут", 1, ("R1", 1), ("R2", 2));
        await c.AddRelayTeamAsync("R", 102, "Вектор", 1, ("S1", 1), ("S2", 2));
        var day = c.Day(1).Id;
        Guid R = c.Groups["R"].Id, Q = c.Groups["Q"].Id;
        await Store.UpsertLegCourseAssignmentsAsync(c.Folder, day,
        [
            new LegCourseAssignment { EventDayId = day, GroupId = R, TeamNumber = 101, Leg = 1, CourseKey = "A" },
            new LegCourseAssignment { EventDayId = day, GroupId = R, TeamNumber = 101, Leg = 2, CourseKey = "B" },
            new LegCourseAssignment { EventDayId = day, GroupId = Q, TeamNumber = 102, Leg = 1, CourseKey = "X" },
        ]);
        await OpenCompetitionAsync(c.Summary.Identifier);

        await _app.OnUiAsync(() => _app.Vm.OpenNumberVariantsCommand.ExecuteAsync(null));
        await _app.SettleAsync();
        var page = Assert.IsType<NumberVariantsViewModel>(Shell().SelectedPage);
        await _app.WaitUntilAsync(() => page.Rows.Count == 3 && page.Unassigned.Count == 1, what: "three rows and one without");
        // A relay competition: «Змагання → Естафета» with «Команди»; the page shows the team columns.
        await _app.WaitUntilAsync(() => _app.Vm.HasRelayGroups, what: "relay menu");
        Assert.Equal(_app.Vm.Localization.Get("Menu.Competition.Relay"), _app.Vm.RelayMenuHeader);
        Assert.True(page.ShowsTeams);
        Assert.Contains(_app.Vm.Localization.Get("NumberVariants.Col.Team"), await HeadersAsync("Sheet"));

        // Above: 102.1 holds Q's variation while team 102 runs in R — both group cells flagged.
        var flagged = page.Rows.Single(r => r.Bib == "102.1");
        Assert.True(flagged.GroupMismatch);
        Assert.Equal(("Q", "R", "Вектор", "S1"), (flagged.GroupName, flagged.HolderGroupName, flagged.TeamName, flagged.Runner));
        Assert.All(page.Rows.Where(r => r.Bib != "102.1"), r => Assert.False(r.GroupMismatch));
        Assert.True(page.SummaryIsWarning);

        // Below: 102.2, offered R's variations (plus «—»).
        var missing = page.Unassigned.Single();
        Assert.Equal(("102.2", "R", true), (missing.Bib, missing.GroupName, missing.IsMissing));
        Assert.Equal(["", "A", "B"], missing.VariantOptions.Select(o => o.Code));
        var sheets = await _app.OnUiAsync(() => _app.Window.GetVisualDescendants().OfType<SheetTable>().Select(t => t.Name).ToList());
        Assert.Contains("UnassignedSheet", sheets);
        await _app.ScreenshotAsync("number-variants");

        // Picking a variation saves it at once; the row stays where it is.
        await _app.OnUiAsync(() => missing.SelectedVariant = missing.VariantOptions.Single(o => o.Code == "A"));
        await _app.WaitUntilAsync(() => Assignment(c, 102, 2) is { CourseKey: "A" } a && a.GroupId == R, what: "102.2 saved");
        Assert.False(missing.IsMissing);
        Assert.Equal("31 32", missing.CourseOrder);

        // F8 over the flagged row: the number goes to the team's group (X is not one of R's, so it shows as unknown).
        Assert.NotNull(page.MoveToHolderGroupLabel([flagged]));
        Assert.Null(page.MoveToHolderGroupLabel([page.Rows.Single(r => r.Bib == "101.1")]));
        await _app.OnUiAsync(() => page.MoveToHolderGroupAsync([flagged]));
        await _app.SettleAsync();
        Assert.Equal(R, Assignment(c, 102, 1)!.GroupId);
        await _app.WaitUntilAsync(() => page.Rows.Count == 4 && page.Unassigned.Count == 0, what: "reloaded after the move");
        var moved = page.Rows.Single(r => r.Bib == "102.1");
        Assert.Equal((false, true), (moved.GroupMismatch, moved.IsUnknown));
        await _app.ScreenshotAsync("number-variants-after");
        AssertNoUiErrors();
    }

    [Fact]
    public async Task ScatterByNumber_IsItsOwnMenuWithoutTheTeamColumns()
    {
        var c = await CompetitionBuilder.CreateAsync(Core, TestIds.Next("ui_numvar_sc"), discipline: DisciplineId.Scatter);
        await c.AddControlsAsync(1, "31", "32");
        await c.AddGroupAsync("Ж16", "31 32");
        await c.SetCourseVariantsAsync("Ж16", 1, ("A", "31 32"), ("B", "32 31"));
        await c.AddRunnerAsync("Петренко", "21", "2101", "Ж16");
        await c.AddRunnerAsync("Коваль", "22", "2201", "Ж16");
        var day = c.Day(1).Id;
        await Store.UpsertLegCourseAssignmentsAsync(c.Folder, day,
            [new LegCourseAssignment { EventDayId = day, GroupId = c.Groups["Ж16"].Id, TeamNumber = 21, Leg = 1, CourseKey = "B" }]);
        await OpenCompetitionAsync(c.Summary.Identifier);

        // No relay group, but numbers with variants: «Змагання → Розсіювання → Варіанти за номерами», no «Команди».
        await _app.WaitUntilAsync(() => _app.Vm.IsRelayMenuVisible, what: "scatter menu");
        Assert.False(_app.Vm.HasRelayGroups);
        Assert.Equal(_app.Vm.Localization.Get("Menu.Competition.Scatter"), _app.Vm.RelayMenuHeader);

        await _app.OnUiAsync(() => _app.Vm.OpenNumberVariantsCommand.ExecuteAsync(null));
        await _app.SettleAsync();
        var page = Assert.IsType<NumberVariantsViewModel>(Shell().SelectedPage);
        await _app.WaitUntilAsync(() => page.Rows.Count == 1 && page.Unassigned.Count == 1, what: "21 with a variation, 22 without");
        Assert.False(page.ShowsTeams);
        Assert.Equal(_app.Vm.Localization.Get("Page.NumberVariants.Text.Scatter"), page.Text);
        Assert.Equal(_app.Vm.Localization.Get("NumberVariants.Unassigned.Hint.Scatter"), page.UnassignedHint);
        Assert.Equal(("21", "B", "Петренко"), (page.Rows[0].Bib, page.Rows[0].Course, page.Rows[0].Runner));

        // Neither table has a «Команда» column; the holder's group is the runner's.
        var team = _app.Vm.Localization.Get("NumberVariants.Col.Team");
        var upper = await HeadersAsync("Sheet");
        Assert.DoesNotContain(team, upper);
        Assert.Contains(_app.Vm.Localization.Get("NumberVariants.Col.HolderGroup.Scatter"), upper);
        Assert.DoesNotContain(team, await HeadersAsync("UnassignedSheet"));
        await _app.ScreenshotAsync("number-variants-scatter");
        AssertNoUiErrors();
    }

    // The column headers of one of the page's tables, as built.
    private Task<List<string>> HeadersAsync(string sheetName) => _app.OnUiAsync(() => _app.Window
        .GetVisualDescendants().OfType<SheetTable>().Single(t => t.Name == sheetName)
        .Bands!.SelectMany(b => b.Columns).Select(col => col.Header).ToList());

    private static LegCourseAssignment? Assignment(CompetitionBuilder c, int number, int leg) =>
        Store.GetLegCourseAssignmentsAsync(c.Folder, c.Day(1).Id).GetAwaiter().GetResult()
            .SingleOrDefault(a => a.TeamNumber == number && a.Leg == leg);

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
