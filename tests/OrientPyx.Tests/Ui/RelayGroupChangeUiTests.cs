using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.Presentation.ViewModels;
using OrientPyx.Presentation.ViewModels.Dialogs;
using OrientPyx.Presentation.ViewModels.Pages;
using OrientPyx.Tests.Support;

namespace OrientPyx.Tests.Ui;

/// <summary>
/// The group cell of a relay runner (Р-22: their group is the team's) is editable and asks: cancel, take only this runner
/// off the team and move them, or move the whole team — the last only into another relay group. A paste / bulk edit
/// leaves them alone; «не бере участі» in the roster takes them off the team and the day.
/// </summary>
[Collection(AppCollection.Name)]
public sealed class RelayGroupChangeUiTests
{
    private const string Title = "Participants.Relay.GroupChange.Title";

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

        // A paste / bulk edit leaves a relay runner in the team's group.
        await _app.OnUiAsync(() => Row("A").BulkGroup = Option(Row("A"), "I"));
        Assert.Equal("R", await _app.OnUiAsync(() => Row("A").SelectedGroup.Label));

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

    // Waits for the group-change question (by message key, when given), answers it and returns it.
    private async Task<ConfirmDialogViewModel> AnswerAsync(ConfirmDialogResult answer, string? messageKey = null)
    {
        await _app.WaitUntilAsync(() => _app.Vm.Dialogs.Current is ConfirmDialogViewModel d && d.TitleKey == Title
            && (messageKey is null || d.MessageKey == messageKey), what: messageKey ?? Title);
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
