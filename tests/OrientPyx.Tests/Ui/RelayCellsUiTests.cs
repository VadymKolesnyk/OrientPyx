using System.Reflection;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.Presentation.Controls;
using OrientPyx.Presentation.ViewModels;
using OrientPyx.Presentation.ViewModels.Pages;
using OrientPyx.Tests.Support;

namespace OrientPyx.Tests.Ui;

/// <summary>
/// The relay cells of «Учасники» on the real page: the day table shows «Номер етапу» beside «Номер» and the team /
/// leg / name columns, the roster shows «Номер етапу» beside «Номер»; typing «101.3» puts a runner on the leg (saved,
/// rows reloaded), typing a teammate's leg swaps the two, and a leg outside the group's count goes back.
/// </summary>
[Collection(AppCollection.Name)]
public sealed class RelayCellsUiTests
{
    private readonly HeadlessApp _app;

    public RelayCellsUiTests(HeadlessApp app) => _app = app;

    private static IServiceProvider Core => TestEnvironment.Instance.Services;
    private static IEventStore Store => Core.GetRequiredService<IEventStore>();

    [Fact]
    public async Task RelayCellsPlaceSwapAndRefuseOnTheParticipantsPage()
    {
        // Two days: with a single day the page has no day picker and always shows «Мандатка» (a reload goes back to it).
        var c = await CompetitionBuilder.CreateAsync(Core, TestIds.Next("ui_relay"), dayCount: 2, discipline: DisciplineId.Relay);
        await c.AddControlsAsync(1, "31");
        await c.AddGroupAsync("R", "31", s => s.LegCount = 3);
        foreach (var name in new[] { "A", "B", "C" })
            await c.AddRunnerAsync(name, name == "A" ? "17" : "", "", "R");
        await c.AddRelayTeamAsync("R", 101, "Азимут", 1, ("A", 1), ("B", 2));
        await OpenCompetitionAsync(c.Summary.Identifier);

        await _app.OnUiAsync(() => _app.Vm.OpenParticipantsCommand.ExecuteAsync(null));
        await _app.SettleAsync();
        var page = Assert.IsType<ParticipantsViewModel>(Shell().SelectedPage);

        // The roster: «Номер етапу» right after «Номер».
        if (!page.IsRosterMode)
        {
            await _app.OnUiAsync(() => page.SelectedDay = page.DayOptions.First(d => d.IsRoster));
            await _app.SettleAsync();
        }
        await _app.WaitUntilAsync(() => page.Roster.Count == 3, what: "three roster rows");
        var roster = await _app.OnUiAsync(() =>
            _app.Window.GetVisualDescendants().OfType<SheetTable>().Single(t => t.Name == "RosterTable"));
        await _app.WaitUntilAsync(() => BuiltBands(roster).SelectMany(b => b.Columns).Any(col => col.Key == "block:Relay"),
            what: "roster relay column");
        var rosterKeys = await _app.OnUiAsync(() => BuiltBands(roster).SelectMany(b => b.Columns).Select(col => col.Key).Take(2).ToList());
        Assert.Equal(["id:Participants.Col.Number", "block:Relay"], rosterKeys);
        var allRosterKeys = await _app.OnUiAsync(() => BuiltBands(roster).SelectMany(b => b.Columns).Select(col => col.Key).ToList());
        Assert.Contains("block:RelayTeam", allRosterKeys);              // «Команда», like a rogaine team
        Assert.Contains("block:RelayTeamNumber", allRosterKeys);
        Assert.Contains("block:RelayLeg", allRosterKeys);
        Assert.Equal("101.1", page.Roster.Single(r => r.FullName == "A").Days[0].Relay.BibText);

        // The day table: «Номер етапу» beside «Номер», then «Команда» / «Номер команди» / «Етап» after «Група».
        await _app.OnUiAsync(() => page.SelectedDay = page.DayOptions.First(d => !d.IsRoster));
        await _app.SettleAsync();
        await _app.WaitUntilAsync(() => page.IsDayMode && page.Participants.Count == 3, what: "three day rows");
        var keys = page.DayBands.SelectMany(b => b.Columns).Select(col => col.Key).ToList();
        Assert.Equal(["id:Participants.Col.Number", "id:Participants.Col.RelayBib"], keys.Take(2));
        Assert.Contains("id:Participants.Col.RelayTeam", keys);
        Assert.Contains("id:Participants.Col.RelayLeg", keys);
        Assert.Contains("id:Participants.Col.RelayTeamName", keys);

        // Read on the UI thread's collection from the test thread: a row may be missing for a moment while it reloads.
        ParticipantDayRowViewModel Row(string name) =>
            page.Participants.ToList().FirstOrDefault(r => r.FullName == name) ?? throw new InvalidOperationException("no row " + name);
        bool RowIs(string name, Func<ParticipantDayRowViewModel, bool> check) =>
            page.Participants.ToList().FirstOrDefault(r => r.FullName == name) is { } row && check(row);
        var rowC = Row("C");
        Assert.False(Row("A").IsGroupEditable);                 // a relay runner's group is the team's
        Assert.True(Row("C").IsGroupEditable);

        // «Азимут» typed in C's «Команда»: C joins the team on the free leg 3; «Етап» was amber (2 of 3) and isn't now.
        Assert.True(Row("A").Relay.IsIncomplete);
        await _app.OnUiAsync(() => Row("C").Relay.TeamName = "Азимут");
        await _app.WaitUntilAsync(() => LegOf(c, "C") == 3, what: "C on leg 3 in the store");
        await _app.WaitUntilAsync(() => RowIs("C", r => r.Relay.BibText == "101.3" && r.Relay.TeamText == "101"),
            what: "C's row shows 101.3");
        await _app.WaitUntilAsync(() => RowIs("A", r => !r.Relay.IsIncomplete), what: "the team is complete");
        Assert.Same(rowC, Row("C"));                            // merged into the same row, not a rebuilt table

        // A takes leg 2 of their own team: A and B swap.
        await _app.OnUiAsync(() => Row("A").Relay.LegText = "2");
        await _app.WaitUntilAsync(() => LegOf(c, "A") == 2 && LegOf(c, "B") == 1, what: "A and B swapped");
        await _app.WaitUntilAsync(() => RowIs("B", r => r.Relay.BibText == "101.1"), what: "B's row reloaded");

        // A leg the group doesn't have is refused: the cell goes back to the stored leg.
        await _app.OnUiAsync(() => Row("B").Relay.LegText = "4");
        await _app.WaitUntilAsync(() => RowIs("B", r => r.Relay.LegText == "1"), what: "refused edit reverted");
        Assert.Equal(1, LegOf(c, "B"));

        // «Номер команди» belongs to the team: the change is asked, then the team is renumbered for every runner.
        await _app.OnUiAsync(() => Row("A").Relay.TeamText = "105");
        var question = await AnswerAsync("Participants.Relay.Renumber.Title", confirm: true);
        Assert.Contains("105", question);
        await _app.WaitUntilAsync(() => RowIs("C", r => r.Relay.BibText == "105.3"), what: "team renumbered on every row");

        // A new name for a team with teammates: asked too — «Перейменувати команду» renames it for all of them.
        await _app.OnUiAsync(() => Row("A").Relay.TeamName = "Полісся");
        await AnswerAsync("Participants.Relay.Rename.Title", confirm: true);
        await _app.WaitUntilAsync(() => RowIs("C", r => r.Relay.TeamName == "Полісся"), what: "team renamed on every row");

        // Cancelling the question leaves everything as it was.
        await _app.OnUiAsync(() => Row("B").Relay.TeamText = "300");
        await AnswerAsync("Participants.Relay.Renumber.Title", confirm: false);
        await _app.WaitUntilAsync(() => RowIs("B", r => r.Relay.TeamText == "105"), what: "cancelled edit reverted");

        await _app.ScreenshotAsync("relay-cells");
        AssertNoUiErrors();
    }

    // Waits for the confirmation with the given title, answers it, and returns its message.
    private async Task<string> AnswerAsync(string titleKey, bool confirm)
    {
        await _app.WaitUntilAsync(() => _app.Vm.Dialogs.Current is OrientPyx.Presentation.ViewModels.Dialogs.ConfirmDialogViewModel d
            && d.TitleKey == titleKey, what: titleKey);
        var dialog = (OrientPyx.Presentation.ViewModels.Dialogs.ConfirmDialogViewModel)_app.Vm.Dialogs.Current!;
        await _app.ScreenshotAsync("relay-" + titleKey.Split('.')[^2].ToLowerInvariant());
        await _app.OnUiAsync(() =>
        {
            if (confirm)
                dialog.ConfirmCommand.Execute(null);
            else
                dialog.CancelCommand.Execute(null);
        });
        await _app.WaitUntilAsync(() => _app.Vm.Dialogs.Current is null, what: "question closed");
        return dialog.Message;
    }

    // The roster builds its own bands from the day set and the blocks (it has no Bands of its own).
    private static IReadOnlyList<SheetBand> BuiltBands(SheetTable table) =>
        (IReadOnlyList<SheetBand>)typeof(SheetTable).GetField("_bands", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(table)!;

    private static int? LegOf(CompetitionBuilder c, string runner) =>
        Store.GetParticipantDaysAsync(c.Folder, c.Day(1).Id).GetAwaiter().GetResult()
            .Single(l => l.ParticipantId == c.Runners[runner].Id).Leg;

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
