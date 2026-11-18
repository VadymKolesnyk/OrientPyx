using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.Presentation.ViewModels;
using OrientPyx.Presentation.ViewModels.Dialogs;
using OrientPyx.Presentation.ViewModels.Pages;
using OrientPyx.Tests.Support;

namespace OrientPyx.Tests.Ui;

/// <summary>
/// Ctrl+Z / Ctrl+Y on «Команди», «Групи» and «Варіанти за номерами» (the real pages over the real store): an edit made
/// in the page is undone as one step — the page shows the value it had and the database holds it again.
/// </summary>
[Collection(AppCollection.Name)]
public sealed class UndoPagesTests : IAsyncLifetime
{
    // Undo / redo toasts stay up for 15 s: cleared before and after each test, so a stack of them never covers the
    // table of the next test in the shared headless window.
    public Task InitializeAsync() => ClearToastsAsync();

    public Task DisposeAsync() => ClearToastsAsync();

    private Task ClearToastsAsync() => _app.OnUiAsync(() => _app.Vm.Toasts.Items.Clear());

    private readonly HeadlessApp _app;

    public UndoPagesTests(HeadlessApp app) => _app = app;

    private static IServiceProvider Core => TestEnvironment.Instance.Services;
    private static IEventStore Store => Core.GetRequiredService<IEventStore>();

    [Fact]
    public async Task RelayTeams_TeamNumberChangeForAllRunners_IsOneUndoStep()
    {
        var c = await CompetitionBuilder.CreateAsync(Core, TestIds.Next("ui_undo_teams"), discipline: DisciplineId.Relay);
        await c.AddGroupAsync("R", "31", s => s.LegCount = 2);
        await c.AddRunnerAsync("Перший", "", "", "R");
        await c.AddRunnerAsync("Другий", "", "", "R");
        await c.AddRelayTeamAsync("R", 101, "Азимут", 1, ("Перший", 1), ("Другий", 2));
        await OpenCompetitionAsync(c.Summary.Identifier);

        await _app.OnUiAsync(() => _app.Vm.OpenRelayTeamsCommand.ExecuteAsync(null));
        await _app.SettleAsync();
        var page = Assert.IsType<RelayTeamsViewModel>(Shell().SelectedPage);
        await _app.WaitUntilAsync(() => page.Teams.Count == 1 && page.Legs.Count == 2, what: "the team and its legs");
        var team = page.Teams[0];
        Assert.Equal(["Етап 1 · 101.1", "Етап 2 · 101.2"], page.Legs.Select(l => l.Header));

        // The number belongs to the team: every runner's leg number follows it.
        await _app.OnUiAsync(() => team.NumberText = "105");
        await WaitForAsync(async () => (await TeamNumberAsync(c)) == 105, "105 saved");
        await _app.OnUiAsync(() => _app.Vm.EditMenu.Refresh());
        Assert.True(_app.Vm.EditMenu.CanUndo);
        Assert.Contains("101 → 105", _app.Vm.EditMenu.UndoHeader);

        // Ctrl+Z from the keyboard (focus on a button, not in a text box): one step puts 101 back for the whole team.
        await _app.OnUiAsync(() =>
        {
            _app.Window.GetVisualDescendants().OfType<Button>()
                .First(b => b.IsEffectivelyVisible && b.IsEffectivelyEnabled && b.Focusable).Focus();
            Assert.IsNotType<TextBox>(_app.Window.FocusManager?.GetFocusedElement());
            _app.Window.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");
        });
        await WaitForAsync(async () => (await TeamNumberAsync(c)) == 101, "101 back in the database");
        await _app.SettleAsync();
        await _app.WaitUntilAsync(() => team.NumberText == "101", what: "101 back in the table");
        Assert.Same(team, page.Teams.Single());   // merged in place: the row (and the selection) stays
        Assert.Equal(["Етап 1 · 101.1", "Етап 2 · 101.2"], page.Legs.Select(l => l.Header));
        await _app.OnUiAsync(() => _app.Vm.EditMenu.Refresh());
        Assert.False(_app.Vm.EditMenu.CanUndo);
        Assert.True(_app.Vm.EditMenu.CanRedo);

        // Ctrl+Y does it again.
        await _app.OnUiAsync(() => _app.Vm.EditMenu.RedoCommand.ExecuteAsync(null));
        await WaitForAsync(async () => (await TeamNumberAsync(c)) == 105, "105 redone");
        await _app.WaitUntilAsync(() => team.NumberText == "105", what: "105 redone in the table");
        AssertNoUiErrors();
    }

    [Fact]
    public async Task RelayTeams_DeletedTeamComesBackWithItsRunners()
    {
        var c = await CompetitionBuilder.CreateAsync(Core, TestIds.Next("ui_undo_teamdel"), discipline: DisciplineId.Relay);
        await c.AddGroupAsync("R", "31", s => s.LegCount = 2);
        await c.AddRunnerAsync("Перший", "", "", "R");
        await c.AddRunnerAsync("Другий", "", "", "R");
        await c.AddRelayTeamAsync("R", 101, "Азимут", 1, ("Перший", 1), ("Другий", 2));
        await OpenCompetitionAsync(c.Summary.Identifier);

        await _app.OnUiAsync(() => _app.Vm.OpenRelayTeamsCommand.ExecuteAsync(null));
        await _app.SettleAsync();
        var page = Assert.IsType<RelayTeamsViewModel>(Shell().SelectedPage);
        await _app.WaitUntilAsync(() => page.Teams.Count == 1, what: "the team listed");

        await _app.OnUiAsync(() => page.DeleteTeamNoConfirmAsync(page.Teams[0]));
        await _app.SettleAsync();
        Assert.Empty(page.Teams);
        Assert.Empty(await Store.GetRelayTeamsAsync(c.Folder, c.Day(1).Id));

        await UndoAsync();
        await _app.WaitUntilAsync(() => page.Teams.Count == 1, what: "the team back in the table");
        var back = Assert.Single(await Store.GetRelayTeamsAsync(c.Folder, c.Day(1).Id));
        Assert.Equal((101, "Азимут"), (back.Number, back.Name));
        Assert.Equal(back.Id, (await c.GetLinkAsync("Другий", 1)).RelayTeamId);
        Assert.Equal(2, (await c.GetLinkAsync("Другий", 1)).Leg);
        AssertNoUiErrors();
    }

    [Fact]
    public async Task Groups_CellEditAndDeleteAreUndone()
    {
        var c = await CompetitionBuilder.CreateAsync(Core, TestIds.Next("ui_undo_groups"));
        await c.AddControlsAsync(1, "31", "32", "33");
        await c.AddGroupAsync("Ж21", "31 32");
        await c.AddGroupAsync("Ч21", "33");
        await OpenCompetitionAsync(c.Summary.Identifier);

        await _app.OnUiAsync(() => _app.Vm.OpenGroupsCommand.ExecuteAsync(null));
        await _app.SettleAsync();
        var page = Assert.IsType<GroupsViewModel>(Shell().SelectedPage);
        await _app.WaitUntilAsync(() => page.Groups.Count == 2, what: "two groups listed");
        var row = page.Groups.Single(g => g.Name == "Ж21");

        // A cell edit (debounced write) …
        await _app.OnUiAsync(() => row.CourseOrder = "31 32 33");
        await WaitForAsync(async () => (await CourseAsync(c, "Ж21")) == "31 32 33", "the course saved");
        await _app.OnUiAsync(() => _app.Vm.EditMenu.Refresh());
        Assert.Contains("Ж21", _app.Vm.EditMenu.UndoHeader);

        // … is undone: the database and the row hold the old course again.
        await UndoAsync();
        await WaitForAsync(async () => (await CourseAsync(c, "Ж21")) == "31 32", "the course undone");
        await _app.WaitUntilAsync(() => row.CourseOrder == "31 32", what: "the old course in the table");
        Assert.Same(row, page.Groups.Single(g => g.Name == "Ж21"));

        // Removing a group from the day (optimistic: the row goes at once) comes back with its settings.
        var ch = page.Groups.Single(g => g.Name == "Ч21");
        await _app.OnUiAsync(() => page.DeleteGroupNoConfirmAsync(ch));
        await WaitForAsync(async () => (await Store.GetGroupDaySettingsAsync(c.Folder, c.Day(1).Id)).Count == 1, "Ч21 removed");
        Assert.DoesNotContain(page.Groups, g => g.Name == "Ч21");

        await UndoAsync();
        await WaitForAsync(async () => (await CourseAsync(c, "Ч21")) == "33", "Ч21 back on the day");
        await _app.WaitUntilAsync(() => page.Groups.Any(g => g.Name == "Ч21"), what: "Ч21 back in the table");
        AssertNoUiErrors();
    }

    [Fact]
    public async Task Groups_MergeByRenameIsUndone()
    {
        var c = await CompetitionBuilder.CreateAsync(Core, TestIds.Next("ui_undo_merge"));
        await c.AddControlsAsync(1, "31", "32");
        await c.AddGroupAsync("Ж21", "31 32");
        await c.AddGroupAsync("Ж20", "32");
        await c.AddRunnerAsync("Бігунка", "", "", "Ж21");
        var source = c.Groups["Ж21"].Id;
        await OpenCompetitionAsync(c.Summary.Identifier);

        await _app.OnUiAsync(() => _app.Vm.OpenGroupsCommand.ExecuteAsync(null));
        await _app.SettleAsync();
        var page = Assert.IsType<GroupsViewModel>(Shell().SelectedPage);
        await _app.WaitUntilAsync(() => page.Groups.Count == 2, what: "two groups listed");

        // Renaming onto another group's name merges them, once the question is answered.
        await _app.OnUiAsync(() => page.Groups.Single(g => g.Name == "Ж21").Name = "Ж20");
        await _app.WaitUntilAsync(() => _app.Vm.Dialogs.Current is ConfirmDialogViewModel, what: "the merge question");
        await _app.OnUiAsync(() => ((ConfirmDialogViewModel)_app.Vm.Dialogs.Current!).ConfirmCommand.Execute(null));
        await WaitForAsync(async () => (await Store.GetGroupsAsync(c.Folder)).Count == 1, "merged");
        await _app.SettleAsync();
        await _app.WaitUntilAsync(() => page.Groups.Count == 1, what: "one group left in the table");
        Assert.NotEqual(source, (await c.GetLinkAsync("Бігунка", 1)).GroupId);

        // One undo splits them again: the group, its day settings and its runner are back.
        await UndoAsync();
        await WaitForAsync(async () => (await Store.GetGroupsAsync(c.Folder)).Count == 2, "the merge undone");
        Assert.Equal(source, (await c.GetLinkAsync("Бігунка", 1)).GroupId);
        Assert.Equal("31 32", await CourseAsync(c, "Ж21"));
        await _app.WaitUntilAsync(() => page.Groups.Count == 2 && page.Groups.Any(g => g.Name == "Ж21"),
            what: "both groups in the table");
        AssertNoUiErrors();
    }

    [Fact]
    public async Task NumberVariants_PickAndMoveToTeamGroupAreUndone()
    {
        var c = await CompetitionBuilder.CreateAsync(Core, TestIds.Next("ui_undo_numvar"), discipline: DisciplineId.Relay);
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

        // A variation picked for 102.2 is saved at once …
        var missing = page.Unassigned.Single();
        await _app.OnUiAsync(() => missing.SelectedVariant = missing.VariantOptions.Single(o => o.Code == "A"));
        await WaitForAsync(async () => (await AssignmentAsync(c, 102, 2))?.CourseKey == "A", "102.2 saved");
        await _app.OnUiAsync(() => _app.Vm.EditMenu.Refresh());
        Assert.Contains("102.2", _app.Vm.EditMenu.UndoHeader);

        // … and undone: no variation for 102.2 again, the page lists it among the numbers without one.
        await UndoAsync();
        await WaitForAsync(async () => await AssignmentAsync(c, 102, 2) is null, "102.2 undone");
        await _app.WaitUntilAsync(() => page.Unassigned.Count == 1 && page.Unassigned[0].IsMissing, what: "102.2 without a variation");

        // F8 «Перенести в групу команди» over the flagged row, undone: the number is Q's again.
        var flagged = page.Rows.Single(r => r.Bib == "102.1");
        await _app.OnUiAsync(() => page.MoveToHolderGroupAsync([flagged]));
        await _app.SettleAsync();
        Assert.Equal(R, (await AssignmentAsync(c, 102, 1))!.GroupId);

        await UndoAsync();
        await WaitForAsync(async () => (await AssignmentAsync(c, 102, 1))?.GroupId == Q, "the move undone");
        await _app.WaitUntilAsync(() => page.Rows.SingleOrDefault(r => r.Bib == "102.1") is { GroupMismatch: true },
            what: "102.1 flagged again");
        AssertNoUiErrors();
    }

    // «Правка → Скасувати» for the shown page, then the busy overlay (the reload after the undo runs under it).
    private async Task UndoAsync()
    {
        await _app.OnUiAsync(() => _app.Vm.EditMenu.Refresh());
        Assert.True(_app.Vm.EditMenu.CanUndo, "nothing to undo");
        await _app.OnUiAsync(() => _app.Vm.EditMenu.UndoCommand.ExecuteAsync(null));
        await _app.SettleAsync();
    }

    // Polled off the UI thread: blocking it on a store call would deadlock.
    private static async Task WaitForAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Not in time: {what}");
            await Task.Delay(100);
        }
    }

    private static async Task<int?> TeamNumberAsync(CompetitionBuilder c)
        => (await Store.GetRelayTeamsAsync(c.Folder, c.Day(1).Id)).Single().Number;

    private static async Task<string?> CourseAsync(CompetitionBuilder c, string group)
        => (await Store.GetGroupDaySettingsAsync(c.Folder, c.Day(1).Id))
            .SingleOrDefault(s => s.GroupId == c.Groups[group].Id)?.CourseOrder;

    private static async Task<LegCourseAssignment?> AssignmentAsync(CompetitionBuilder c, int number, int leg)
        => (await Store.GetLegCourseAssignmentsAsync(c.Folder, c.Day(1).Id))
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
