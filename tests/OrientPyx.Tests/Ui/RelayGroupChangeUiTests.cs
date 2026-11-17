using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.Presentation.ViewModels;
using OrientPyx.Presentation.ViewModels.Dialogs;
using OrientPyx.Presentation.ViewModels.Pages;
using OrientPyx.Tests.Support;

namespace OrientPyx.Tests.Ui;

/// <summary>
/// The group cell of a relay runner (Р-22: their group is the team's) is editable and asks: cancel, take only this runner
/// off the team and move them, or move the whole team — the last only into another relay group. «не бере участі» in the
/// roster takes them off the team and the day. A paste / bulk edit moves a team it covers wholly into a relay group
/// without a question and asks once about the rest. A team whose leg numbers hold courses of its group is asked about
/// before it moves (here and on «Команди»).
/// </summary>
[Collection(AppCollection.Name)]
public sealed class RelayGroupChangeUiTests
{
    private const string Title = "Participants.Relay.GroupChange.Title";
    private const string BulkTitle = "Participants.Relay.BulkGroup.Title";
    private const string CoursesTitle = "Relay.NumberCourses.Title";

    private readonly HeadlessApp _app;

    public RelayGroupChangeUiTests(HeadlessApp app) => _app = app;

    private static IServiceProvider Core => TestEnvironment.Instance.Services;
    private static IEventStore Store => Core.GetRequiredService<IEventStore>();

    [Fact]
    public async Task ARelayRunnersGroupIsAskedAndMovesTheRunnerOrTheTeam()
    {
        // Two days: with a single day the page has no day picker.
        var c = await CompetitionBuilder.CreateAsync(Core, TestIds.Next("ui_relaygroup"), dayCount: 2, discipline: DisciplineId.Relay);
        await c.AddControlsAsync(1, "31");
        await c.AddGroupAsync("R", "31", s => s.LegCount = 3);
        await c.AddGroupAsync("R2", "31", s => s.LegCount = 3);
        await c.AddGroupAsync("I", "31", s => s.DisciplineOverride = DisciplineId.SetCourse);
        foreach (var name in new[] { "A", "B", "C" })
            await c.AddRunnerAsync(name, "", "", "R");
        await c.AddRelayTeamAsync("R", 101, "Азимут", 1, ("A", 1), ("B", 2));
        var day1 = c.Day(1).Id;
        async Task<Guid?> StoredGroupAsync(string runner) =>
            (await Store.GetParticipantDaysAsync(c.Folder, day1)).SingleOrDefault(l => l.ParticipantId == c.Runners[runner].Id)?.GroupId;
        await OpenCompetitionAsync(c.Summary.Identifier);

        await _app.OnUiAsync(() => _app.Vm.OpenParticipantsCommand.ExecuteAsync(null));
        await _app.SettleAsync();
        var page = Assert.IsType<ParticipantsViewModel>(Shell().SelectedPage);
        await _app.OnUiAsync(() => page.SelectedDay = page.DayOptions.First(d => d.Number == 1));
        await _app.SettleAsync();
        await _app.WaitUntilAsync(() => page.IsDayMode && page.Participants.Count == 3, what: "three day rows");
        ParticipantDayRowViewModel Row(string name) => page.Participants.Single(r => r.FullName == name);
        GroupOption Option(ParticipantDayRowViewModel row, string group) => row.GroupOptions.Single(o => o.Label == group);

        // Picking R2 asks (R2 runs a relay: the whole team is offered); cancelled, A stays in R.
        await _app.OnUiAsync(() => Row("A").ComboGroup = Option(Row("A"), "R2"));
        var question = await AnswerAsync(ConfirmDialogResult.Cancel);
        Assert.True(question.ShowAlternate);
        Assert.Equal("Participants.Relay.GroupChange.WholeTeam", question.ConfirmKey);
        await _app.SettleAsync();
        Assert.Equal("R", await _app.OnUiAsync(() => Row("A").ComboGroup?.Label));
        Assert.Equal(c.Groups["R"].Id, await StoredGroupAsync("A"));

        // Again, «Всю команду»: A and B move to R2 together.
        await _app.OnUiAsync(() => Row("A").ComboGroup = Option(Row("A"), "R2"));
        await AnswerAsync(ConfirmDialogResult.Confirm);
        await WaitForAsync(async () => await StoredGroupAsync("B") == c.Groups["R2"].Id, "the team moved to R2");
        Assert.Equal(c.Groups["R2"].Id, await StoredGroupAsync("A"));
        await _app.WaitUntilAsync(() => Row("A").SelectedGroup.Label == "R2" && Row("B").SelectedGroup.Label == "R2",
            what: "both rows show R2");

        // B into I, which runs no relay: only B can go — off the team.
        await _app.OnUiAsync(() => Row("B").ComboGroup = Option(Row("B"), "I"));
        question = await AnswerAsync(ConfirmDialogResult.Confirm);
        Assert.False(question.ShowAlternate);
        Assert.Equal("Participants.Relay.GroupChange.ReleaseAndMove", question.ConfirmKey);
        await WaitForAsync(async () => await StoredGroupAsync("B") == c.Groups["I"].Id, "B moved to I");
        Assert.Null((await Store.GetParticipantDaysAsync(c.Folder, day1)).Single(l => l.ParticipantId == c.Runners["B"].Id).RelayTeamId);
        await _app.WaitUntilAsync(() => !Row("B").GroupFollowsTeam && Row("B").SelectedGroup.Label == "I", what: "B is off the team");
        Assert.Equal(c.Groups["R2"].Id, await StoredGroupAsync("A"));

        // The roster: «не бере участі» for A (still on 101) takes A off the team and day 1 — A runs day 2 too, so no
        // last-day question follows.
        await _app.OnUiAsync(() => page.SelectedDay = page.DayOptions.First(d => d.IsRoster));
        await _app.SettleAsync();
        await _app.WaitUntilAsync(() => page.IsRosterMode && page.Roster.Count == 3, what: "roster rows");
        await _app.OnUiAsync(() =>
        {
            var cell = page.Roster.Single(r => r.FullName == "A").Days[0];
            Assert.True(cell.CanEditGroup);
            cell.ComboGroup = cell.GroupOptions[0];
        });
        await AnswerAsync(ConfirmDialogResult.Confirm, "Participants.Relay.GroupChange.LeaveMessage");
        await WaitForAsync(async () => await StoredGroupAsync("A") is null
            && (await Store.GetParticipantDaysAsync(c.Folder, day1)).All(l => l.ParticipantId != c.Runners["A"].Id), "A left day 1");
        await _app.WaitUntilAsync(() => !page.Roster.Single(r => r.FullName == "A").Days[0].IsMember, what: "A's day 1 cell is empty");
        AssertNoUiErrors();
    }

    [Fact]
    public async Task ABulkGroupChangeMovesWholeTeamsAndAsksAboutTheRest()
    {
        var c = await CompetitionBuilder.CreateAsync(Core, TestIds.Next("ui_relaybulk"), dayCount: 2, discipline: DisciplineId.Relay);
        await c.AddControlsAsync(1, "31");
        await c.AddGroupAsync("R", "31", s => s.LegCount = 2);
        await c.AddGroupAsync("R2", "31", s => s.LegCount = 2);
        await c.AddGroupAsync("I", "31", s => s.DisciplineOverride = DisciplineId.SetCourse);
        foreach (var name in new[] { "A", "B", "C", "D" })
            await c.AddRunnerAsync(name, "", "", "R");
        await c.AddRelayTeamAsync("R", 101, "Азимут", 1, ("A", 1), ("B", 2));
        await c.AddRelayTeamAsync("R", 102, "Вітер", 1, ("C", 1), ("D", 2));
        async Task<ParticipantDay?> LinkAsync(string runner, int day) =>
            (await Store.GetParticipantDaysAsync(c.Folder, c.Day(day).Id)).SingleOrDefault(l => l.ParticipantId == c.Runners[runner].Id);
        await OpenCompetitionAsync(c.Summary.Identifier);

        await _app.OnUiAsync(() => _app.Vm.OpenParticipantsCommand.ExecuteAsync(null));
        await _app.SettleAsync();
        var page = Assert.IsType<ParticipantsViewModel>(Shell().SelectedPage);
        await _app.OnUiAsync(() => page.SelectedDay = page.DayOptions.First(d => d.Number == 1));
        await _app.SettleAsync();
        await _app.WaitUntilAsync(() => page.IsDayMode && page.Participants.Count == 4, what: "four day rows");
        ParticipantDayRowViewModel Row(string name) => page.Participants.Single(r => r.FullName == name);
        GroupOption Option(ParticipantDayRowViewModel row, string group) => row.GroupOptions.Single(o => o.Label == group);

        // A, B (all of 101) and C (one of 102's two) into R2 in one go: 101 moves without a question, only 102 is asked;
        // «Лише цих учасників» takes C off 102.
        await _app.OnUiAsync(() =>
        {
            foreach (var name in new[] { "A", "B", "C" })
                Row(name).BulkGroup = Option(Row(name), "R2");
        });
        var question = await AnswerAsync(ConfirmDialogResult.Alternate, title: BulkTitle);
        Assert.Equal("Participants.Relay.BulkGroup.WholeTeams", question.ConfirmKey);
        Assert.Equal("Participants.Relay.BulkGroup.OnlyRunners", question.AlternateKey);
        var teams = Assert.IsType<string>(question.MessageArgs![0]);
        Assert.StartsWith("102 (", teams);
        Assert.DoesNotContain("101", teams);
        await WaitForAsync(async () => (await LinkAsync("C", 1))?.GroupId == c.Groups["R2"].Id
            && (await LinkAsync("B", 1))?.GroupId == c.Groups["R2"].Id, "101 and C moved to R2");
        Assert.NotNull((await LinkAsync("A", 1))!.RelayTeamId);
        Assert.NotNull((await LinkAsync("B", 1))!.RelayTeamId);
        Assert.Null((await LinkAsync("C", 1))!.RelayTeamId);
        Assert.Equal(c.Groups["R"].Id, (await LinkAsync("D", 1))!.GroupId);
        await _app.WaitUntilAsync(() => Row("A").SelectedGroup.Label == "R2" && Row("B").SelectedGroup.Label == "R2"
            && Row("C").SelectedGroup.Label == "R2" && !Row("C").GroupFollowsTeam && Row("D").SelectedGroup.Label == "R",
            what: "the rows show the moves");

        // D alone on 102 into I, which runs no relay: asked even though the whole team is picked; kept.
        await _app.OnUiAsync(() => Row("D").BulkGroup = Option(Row("D"), "I"));
        question = await AnswerAsync(ConfirmDialogResult.Cancel, title: BulkTitle);
        Assert.False(question.ShowAlternate);
        Assert.Equal("Participants.Relay.BulkGroup.ReleaseAndMove", question.ConfirmKey);
        await _app.SettleAsync();
        Assert.Equal(c.Groups["R"].Id, (await LinkAsync("D", 1))!.GroupId);
        Assert.NotNull((await LinkAsync("D", 1))!.RelayTeamId);
        Assert.Equal("R", await _app.OnUiAsync(() => Row("D").SelectedGroup.Label));

        // The roster's bulk edit (every day): day 1 moves 102 (D is all of it) to R2 without a question, day 2 as usual.
        await _app.OnUiAsync(() => page.SelectedDay = page.DayOptions.First(d => d.IsRoster));
        await _app.SettleAsync();
        await _app.WaitUntilAsync(() => page.IsRosterMode && page.Roster.Count == 4, what: "roster rows");
        await _app.OnUiAsync(() => page.Roster.Single(r => r.FullName == "D").SetGroupForAllDays(c.Groups["R2"].Id));
        await WaitForAsync(async () => (await LinkAsync("D", 1))?.GroupId == c.Groups["R2"].Id
            && (await LinkAsync("D", 2))?.GroupId == c.Groups["R2"].Id, "D in R2 on both days");
        Assert.NotNull((await LinkAsync("D", 1))!.RelayTeamId);
        Assert.Null(_app.Vm.Dialogs.Current);
        AssertNoUiErrors();
    }

    [Fact]
    public async Task MovingATeamWithCoursesByNumberIsAskedFirst()
    {
        var c = await CompetitionBuilder.CreateAsync(Core, TestIds.Next("ui_relaynumcourses"), dayCount: 2, discipline: DisciplineId.Relay);
        await c.AddControlsAsync(1, "31");
        await c.AddGroupAsync("R", "31", s => s.LegCount = 2);
        await c.AddGroupAsync("R2", "31", s => s.LegCount = 2);
        foreach (var name in new[] { "A", "B", "C", "D" })
            await c.AddRunnerAsync(name, "", "", "R");
        await c.AddRelayTeamAsync("R", 101, "Азимут", 1, ("A", 1), ("B", 2));
        await c.AddRelayTeamAsync("R", 102, "Вітер", 1, ("C", 1), ("D", 2));
        var day1 = c.Day(1).Id;
        // 101's leg numbers hold courses of R; 102 has none.
        await Store.UpsertLegCourseAssignmentsAsync(c.Folder, day1,
        [
            new LegCourseAssignment { EventDayId = day1, GroupId = c.Groups["R"].Id, TeamNumber = 101, Leg = 2, CourseKey = "B" },
            new LegCourseAssignment { EventDayId = day1, GroupId = c.Groups["R"].Id, TeamNumber = 101, Leg = 1, CourseKey = "A" }
        ]);
        async Task<Guid> TeamGroupAsync(int number) =>
            (await Store.GetRelayTeamsAsync(c.Folder, day1)).Single(t => t.Number == number).GroupId;
        await OpenCompetitionAsync(c.Summary.Identifier);

        await _app.OnUiAsync(() => _app.Vm.OpenParticipantsCommand.ExecuteAsync(null));
        await _app.SettleAsync();
        var page = Assert.IsType<ParticipantsViewModel>(Shell().SelectedPage);
        await _app.OnUiAsync(() => page.SelectedDay = page.DayOptions.First(d => d.Number == 1));
        await _app.SettleAsync();
        await _app.WaitUntilAsync(() => page.IsDayMode && page.Participants.Count == 4, what: "four day rows");
        ParticipantDayRowViewModel Row(string name) => page.Participants.Single(r => r.FullName == name);
        GroupOption Option(ParticipantDayRowViewModel row, string group) => row.GroupOptions.Single(o => o.Label == group);

        // «Всю команду» for 101 is asked about its courses by number; cancelled, 101 stays in R.
        await _app.OnUiAsync(() => Row("A").ComboGroup = Option(Row("A"), "R2"));
        await AnswerAsync(ConfirmDialogResult.Confirm);
        var warning = await AnswerAsync(ConfirmDialogResult.Cancel, title: CoursesTitle);
        Assert.Equal("Relay.NumberCourses.Move", warning.ConfirmKey);
        Assert.Equal("101.1 — A, 101.2 — B", warning.MessageArgs![0]);
        Assert.Equal("R2", warning.MessageArgs[1]);
        await _app.WaitUntilAsync(() => Row("A").ComboGroup?.Label == "R", what: "A shows R again");
        Assert.Equal(c.Groups["R"].Id, await TeamGroupAsync(101));

        // A bulk edit of all four into R2: both teams move whole, 101 is asked about once more — kept; 102 moves.
        await _app.OnUiAsync(() =>
        {
            foreach (var name in new[] { "A", "B", "C", "D" })
                Row(name).BulkGroup = Option(Row(name), "R2");
        });
        await AnswerAsync(ConfirmDialogResult.Cancel, title: CoursesTitle);
        await WaitForAsync(async () => await TeamGroupAsync(102) == c.Groups["R2"].Id, "102 moved to R2");
        Assert.Equal(c.Groups["R"].Id, await TeamGroupAsync(101));

        // «Команди»: another group for 101 asks too — cancelled, the row shows R again; «Перевести все одно» moves it.
        await _app.OnUiAsync(() => _app.Vm.OpenRelayTeamsCommand.ExecuteAsync(null));
        await _app.SettleAsync();
        var teams = Assert.IsType<RelayTeamsViewModel>(Shell().SelectedPage);
        await _app.WaitUntilAsync(() => teams.Teams.Count == 2, what: "two team rows");
        RelayTeamRowViewModel Team(int number) => teams.Teams.Single(t => t.Model.Number == number);
        await _app.OnUiAsync(() => Team(101).SelectedGroup = teams.GroupOptions.Single(g => g.Label == "R2"));
        await AnswerAsync(ConfirmDialogResult.Cancel, title: CoursesTitle);
        await _app.WaitUntilAsync(() => Team(101).SelectedGroup?.Label == "R", what: "101 shows R again");
        await _app.SettleAsync();
        Assert.Equal(c.Groups["R"].Id, await TeamGroupAsync(101));

        await _app.OnUiAsync(() => Team(101).SelectedGroup = teams.GroupOptions.Single(g => g.Label == "R2"));
        await AnswerAsync(ConfirmDialogResult.Confirm, title: CoursesTitle);
        await WaitForAsync(async () => await TeamGroupAsync(101) == c.Groups["R2"].Id, "101 moved to R2");
        await _app.WaitUntilAsync(() => Team(101).Model.GroupId == c.Groups["R2"].Id && Team(101).Model.NumberCourses.Count == 0,
            what: "101 reloaded in R2");
        AssertNoUiErrors();
    }

    // Waits for the question (by title and, when given, message key), answers it and returns it.
    private async Task<ConfirmDialogViewModel> AnswerAsync(ConfirmDialogResult answer, string? messageKey = null, string title = Title)
    {
        await _app.WaitUntilAsync(() => _app.Vm.Dialogs.Current is ConfirmDialogViewModel d && d.TitleKey == title
            && (messageKey is null || d.MessageKey == messageKey), what: messageKey ?? title);
        var dialog = (ConfirmDialogViewModel)_app.Vm.Dialogs.Current!;
        await _app.ScreenshotAsync("relay-group-" + answer.ToString().ToLowerInvariant());
        await _app.OnUiAsync(() =>
        {
            switch (answer)
            {
                case ConfirmDialogResult.Confirm: dialog.ConfirmCommand.Execute(null); break;
                case ConfirmDialogResult.Alternate: dialog.AlternateCommand.Execute(null); break;
                default: dialog.CancelCommand.Execute(null); break;
            }
        });
        await _app.WaitUntilAsync(() => _app.Vm.Dialogs.Current is null, what: "question closed");
        return dialog;
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
