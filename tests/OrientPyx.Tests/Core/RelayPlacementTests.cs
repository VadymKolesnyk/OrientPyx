using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Tests.Support;

namespace OrientPyx.Tests.Core;

/// <summary>
/// The participant tables' relay cells (`PlaceRelayRunnerAsync`): typing «100.2», a team number or a leg on a runner's
/// row — team created on first use, the runner's leg kept or the first free one taken, a teammate's leg swapped, a
/// stranger's leg only after confirmation, a full team / a leg out of range refused — and what the rows read back.
/// </summary>
[Collection(AppCollection.Name)]
public sealed class RelayPlacementTests
{
    private static IServiceProvider Sp => TestEnvironment.Instance.Services;

    private static ICompetitionEditorService Editor => Sp.GetRequiredService<ICompetitionEditorService>();

    // One relay day, group R with 3 legs, runners A…E in R; team 101 with A on leg 1 and B on leg 2.
    private static async Task<CompetitionBuilder> BuildAsync()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("relayplace"), discipline: DisciplineId.Relay);
        await c.AddControlsAsync(1, "31", "32");
        await c.AddGroupAsync("R", "31 32", s => s.LegCount = 3);
        foreach (var name in new[] { "A", "B", "C", "D", "E" })
            await c.AddRunnerAsync(name, "", "", "R");
        await c.AddRelayTeamAsync("R", 101, "Азимут", 1, ("A", 1), ("B", 2));
        await c.SelectAsync();
        return c;
    }

    private static Task<RelayPlacementOutcome> PlaceAsync(CompetitionBuilder c, string runner, int? team, int? leg,
        bool replace = false, int day = 1) =>
        Editor.PlaceRelayRunnerAsync(new RelayPlacementRequest(c.Runners[runner].Id, c.Day(day).Id, team, leg, replace));

    // The runner's place as the day grid of the session day reads it.
    private static async Task<(int? Team, int? Leg)> PlaceOfAsync(CompetitionBuilder c, string runner)
    {
        var rows = await Editor.GetParticipantDayRowsAsync();
        var row = rows.Single(r => r.ParticipantId == c.Runners[runner].Id);
        return (row.Relay?.TeamNumber, row.Relay?.Leg);
    }

    [Fact]
    public async Task ANewTeamNumberCreatesTheTeamInTheRunnersGroup()
    {
        var c = await BuildAsync();

        var outcome = await PlaceAsync(c, "C", 205, 2);

        Assert.Equal(RelayPlacementStatus.Placed, outcome.Status);
        Assert.True(outcome.TeamCreated);
        Assert.Equal("205.2", outcome.Place!.Bib);
        var rows = await Editor.GetParticipantDayRowsAsync();
        var row = rows.Single(r => r.ParticipantId == c.Runners["C"].Id);
        Assert.Equal("205.2", row.Relay!.Bib);
        Assert.Equal(3, row.Relay.LegCount);
        Assert.Equal(c.Groups["R"].Id, row.GroupId);
        // The team page sees it too.
        var team = (await Editor.GetRelayTeamsDataAsync()).Teams.Single(t => t.Number == 205);
        Assert.Equal(c.Groups["R"].Id, team.GroupId);
    }

    [Fact]
    public async Task ATeamWithoutALegTakesTheFirstFreeLegOrKeepsTheRunnersOwn()
    {
        var c = await BuildAsync();

        Assert.Equal(RelayPlacementStatus.Placed, (await PlaceAsync(c, "C", 101, null)).Status);
        Assert.Equal((101, 3), await PlaceOfAsync(c, "C"));

        // B moves from 101.2 to a new team 102: the 2nd leg is free there, so B keeps it.
        Assert.Equal(RelayPlacementStatus.Placed, (await PlaceAsync(c, "B", 102, null)).Status);
        Assert.Equal((102, 2), await PlaceOfAsync(c, "B"));

        // Typing the team the runner is already on changes nothing.
        Assert.Equal(RelayPlacementStatus.Unchanged, (await PlaceAsync(c, "C", 101, null)).Status);
    }

    [Fact]
    public async Task ALegATeammateRunsSwapsTheTwo()
    {
        var c = await BuildAsync();

        var outcome = await PlaceAsync(c, "A", 101, 2);

        Assert.Equal(RelayPlacementStatus.Swapped, outcome.Status);
        Assert.Equal("B", outcome.OtherRunner);
        Assert.Equal((101, 2), await PlaceOfAsync(c, "A"));
        Assert.Equal((101, 1), await PlaceOfAsync(c, "B"));
    }

    [Fact]
    public async Task ALegAnotherRunnerHoldsIsTakenOnlyAfterConfirmation()
    {
        var c = await BuildAsync();

        var asked = await PlaceAsync(c, "C", 101, 1);
        Assert.Equal(RelayPlacementStatus.LegTaken, asked.Status);
        Assert.Equal("A", asked.OtherRunner);
        Assert.Equal((null, null), await PlaceOfAsync(c, "C"));

        var replaced = await PlaceAsync(c, "C", 101, 1, replace: true);
        Assert.Equal(RelayPlacementStatus.Placed, replaced.Status);
        Assert.Equal("A", replaced.OtherRunner);
        Assert.Equal((101, 1), await PlaceOfAsync(c, "C"));
        Assert.Equal((null, null), await PlaceOfAsync(c, "A"));   // A stays on the day, without a leg
    }

    [Fact]
    public async Task AFullTeamAndALegOutOfRangeAreRefused()
    {
        var c = await BuildAsync();
        await PlaceAsync(c, "C", 101, 3);

        var full = await PlaceAsync(c, "D", 101, null);
        Assert.Equal(RelayPlacementStatus.TeamFull, full.Status);
        Assert.Equal(3, full.LegCount);

        var outOfRange = await PlaceAsync(c, "D", 101, 4);
        Assert.Equal(RelayPlacementStatus.LegOutOfRange, outOfRange.Status);
        Assert.Equal((null, null), await PlaceOfAsync(c, "D"));
    }

    [Fact]
    public async Task AnotherGroupForATeamRunnerMovesTheRunnerOrTheWholeTeam()
    {
        var c = await BuildAsync();
        await c.AddGroupAsync("R2", "31 32", s => s.LegCount = 3);
        await c.AddGroupAsync("I", "31 32", s => s.DisciplineOverride = DisciplineId.SetCourse);
        Task<RelayPlacementOutcome> ChangeAsync(string runner, string group, RelayTeamChoice choice) =>
            Editor.ChangeRelayRunnerGroupAsync(new RelayGroupEdit(c.Runners[runner].Id, c.Day(1).Id, c.Groups[group].Id, choice));
        async Task<Guid?> GroupOfAsync(string runner) =>
            (await Editor.GetParticipantDayRowsAsync()).Single(r => r.ParticipantId == c.Runners[runner].Id).GroupId;

        // The team's own group, the whole team into a group that runs no relay, a runner on no team: nothing to do.
        Assert.Equal(RelayPlacementStatus.Unchanged, (await ChangeAsync("A", "R", RelayTeamChoice.ChangeTeam)).Status);
        Assert.Equal(RelayPlacementStatus.Unchanged, (await ChangeAsync("A", "I", RelayTeamChoice.ChangeTeam)).Status);
        Assert.Equal(RelayPlacementStatus.Unchanged, (await ChangeAsync("C", "R2", RelayTeamChoice.MoveRunner)).Status);
        Assert.Equal(c.Groups["R"].Id, await GroupOfAsync("A"));

        // The whole team into the other relay group: both runners follow, on their legs.
        var moved = await ChangeAsync("A", "R2", RelayTeamChoice.ChangeTeam);
        Assert.Equal(RelayPlacementStatus.TeamChanged, moved.Status);
        Assert.Equal(2, moved.MemberCount);
        Assert.Equal(c.Groups["R2"].Id, await GroupOfAsync("A"));
        Assert.Equal(c.Groups["R2"].Id, await GroupOfAsync("B"));
        Assert.Equal((101, 1), await PlaceOfAsync(c, "A"));
        Assert.Equal((101, 2), await PlaceOfAsync(c, "B"));
        Assert.Equal(c.Groups["R2"].Id, (await Editor.GetRelayTeamsDataAsync()).Teams.Single(t => t.Number == 101).GroupId);

        // Only B, into the individual group: off the team, still on the day; A stays on 101 in R2.
        var released = await ChangeAsync("B", "I", RelayTeamChoice.MoveRunner);
        Assert.Equal(RelayPlacementStatus.Released, released.Status);
        Assert.Equal((null, null), await PlaceOfAsync(c, "B"));
        Assert.Equal(c.Groups["I"].Id, await GroupOfAsync("B"));
        Assert.Equal((101, 1), await PlaceOfAsync(c, "A"));
        Assert.Equal(c.Groups["R2"].Id, await GroupOfAsync("A"));
    }

    [Fact]
    public async Task AnEmptyCellTakesTheRunnerOffTheTeam()
    {
        var c = await BuildAsync();

        var outcome = await PlaceAsync(c, "B", null, null);

        Assert.Equal(RelayPlacementStatus.Released, outcome.Status);
        Assert.Null(outcome.Place);
        Assert.Equal((null, null), await PlaceOfAsync(c, "B"));
        Assert.Equal(c.Groups["R"].Id, (await c.GetLinkAsync("B", 1)).GroupId);
    }

    [Fact]
    public async Task ANewTeamNeedsARelayGroupToLiveIn()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("relayplace-groups"), discipline: DisciplineId.Relay);
        await c.AddControlsAsync(1, "31");
        await c.AddGroupAsync("R1", "31", s => s.LegCount = 2);
        await c.AddGroupAsync("R2", "31", s => s.LegCount = 2);
        await c.AddGroupAsync("X", "31", s => s.DisciplineOverride = DisciplineId.SetCourse);
        await c.AddRunnerAsync("Solo", "", "", "X");
        await c.AddRunnerAsync("Relay", "", "", "R2");
        await c.SelectAsync();

        Assert.Equal(RelayPlacementStatus.AmbiguousGroup, (await PlaceAsync(c, "Solo", 300, 1)).Status);

        // An existing team takes the runner into its group (Р-22).
        await PlaceAsync(c, "Relay", 300, 1);
        var moved = await PlaceAsync(c, "Solo", 300, 2);
        Assert.Equal(RelayPlacementStatus.Placed, moved.Status);
        Assert.Equal("R2", moved.MovedToGroup);
        Assert.Equal(c.Groups["R2"].Id, (await c.GetLinkAsync("Solo", 1)).GroupId);

        var plain = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("relayplace-none"));
        await plain.AddControlsAsync(1, "31");
        await plain.AddGroupAsync("M", "31");
        await plain.AddRunnerAsync("P", "", "", "M");
        await plain.SelectAsync();
        Assert.Equal(RelayPlacementStatus.NoRelayGroup, (await PlaceAsync(plain, "P", 1, 1)).Status);
    }

    [Fact]
    public async Task TheRosterMarksRelayDaysAndARunnerJoinsTheDayOnALeg()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("relayplace-roster"), dayCount: 2);
        await c.SetDisciplineAsync(2, DisciplineId.Relay);
        await c.AddControlsAsync(1, "31");
        await c.AddControlsAsync(2, "31");
        await c.AddGroupAsync("R", "31", s => s.LegCount = 2);
        await c.AddRunnerAsync("OnlyDay1", "", "", "R", days: [1]);
        await c.SelectAsync(1);

        var outcome = await PlaceAsync(c, "OnlyDay1", 7, 2, day: 2);
        Assert.Equal(RelayPlacementStatus.Placed, outcome.Status);
        Assert.True(outcome.TeamCreated);

        var roster = await Editor.GetParticipantRosterAsync();
        var row = roster.Single(r => r.ParticipantId == c.Runners["OnlyDay1"].Id);
        Assert.False(row.Days.Single(d => d.DayNumber == 1).IsRelayDay);
        var day2 = row.Days.Single(d => d.DayNumber == 2);
        Assert.True(day2.IsRelayDay);
        Assert.True(day2.IsMember);                 // joined the day in the team's group
        Assert.Equal("7.2", day2.Relay!.Bib);
    }

    [Fact]
    public async Task ATeamWithoutANumberIsEditedByIdAndARunnerWithoutALegIsPlacedOrReleased()
    {
        var c = await BuildAsync();
        var store = Sp.GetRequiredService<IEventStore>();
        // An imported team that has no number yet (numbers come later, by hand): C on leg 1, D on it without a leg.
        var team = new BusinessLogic.Entities.RelayTeam
        {
            EventDayId = c.Day(1).Id, GroupId = c.Groups["R"].Id, Number = null, Name = "Полісся"
        };
        await store.AddRelayTeamsAsync(c.Folder, [team]);
        await store.AssignRelayLegAsync(c.Folder, team.Id, 1, (await c.GetLinkAsync("C", 1)).Id);
        await store.AssignRelayLegAsync(c.Folder, team.Id, 3, (await c.GetLinkAsync("D", 1)).Id);
        await SetLegAsync(c, "D", null);

        var rows = await Editor.GetParticipantDayRowsAsync();
        var place = rows.Single(r => r.ParticipantId == c.Runners["C"].Id).Relay!;
        Assert.Null(place.TeamNumber);
        Assert.Equal(string.Empty, place.Bib);
        Assert.Null(rows.Single(r => r.ParticipantId == c.Runners["D"].Id).Relay!.Leg);

        // A leg edit names the team by id: C moves to leg 2 of the same (numberless) team.
        var moved = await Editor.PlaceRelayRunnerAsync(
            new RelayPlacementRequest(c.Runners["C"].Id, c.Day(1).Id, null, 2, TeamId: team.Id));
        Assert.Equal(RelayPlacementStatus.Placed, moved.Status);
        Assert.Equal("«Полісся»", moved.TeamLabel);
        Assert.Equal(2, (await c.GetLinkAsync("C", 1)).Leg);

        // The runner without a leg takes the first free leg of their team by id…
        var placed = await Editor.PlaceRelayRunnerAsync(
            new RelayPlacementRequest(c.Runners["D"].Id, c.Day(1).Id, null, null, TeamId: team.Id));
        Assert.Equal(RelayPlacementStatus.Placed, placed.Status);
        Assert.Equal(1, (await c.GetLinkAsync("D", 1)).Leg);

        // …and a runner without a leg can be taken off the team.
        await SetLegAsync(c, "D", null);
        var released = await PlaceAsync(c, "D", null, null);
        Assert.Equal(RelayPlacementStatus.Released, released.Status);
        var link = await c.GetLinkAsync("D", 1);
        Assert.Null(link.RelayTeamId);
        Assert.Equal(c.Groups["R"].Id, link.GroupId);
    }

    // Leaves a runner on their team without a leg — the state a UOF import gives extra runners («без етапу»), which no
    // store method writes on its own.
    private static async Task SetLegAsync(CompetitionBuilder c, string runner, int? leg)
    {
        var linkId = (await c.GetLinkAsync(runner, 1)).Id;
        await using (var db = new Microsoft.Data.Sqlite.SqliteConnection(
                         $"Data Source={Path.Combine(c.Folder, "event.db")}"))
        {
            await db.OpenAsync();
            var command = db.CreateCommand();
            command.CommandText = "UPDATE ParticipantDays SET Leg = $leg WHERE upper(Id) = $id";
            command.Parameters.AddWithValue("$leg", (object?)leg ?? DBNull.Value);
            command.Parameters.AddWithValue("$id", linkId.ToString().ToUpperInvariant());
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    private static Task<RelayPlacementOutcome> EditTeamAsync(CompetitionBuilder c, string runner, RelayTeamField field,
        string value, RelayTeamChoice choice = RelayTeamChoice.Ask) =>
        Editor.EditRunnerRelayTeamAsync(new RelayTeamCellEdit(c.Runners[runner].Id, c.Day(1).Id, field, value, choice));

    private static async Task<RelayPlacement?> RelayOfAsync(CompetitionBuilder c, string runner) =>
        (await Editor.GetParticipantDayRowsAsync()).Single(r => r.ParticipantId == c.Runners[runner].Id).Relay;

    [Fact]
    public async Task TheTeamCellWorksLikeARogaineTeam()
    {
        var c = await BuildAsync();

        // An existing team's name puts the runner on it (the first free leg).
        Assert.Equal(RelayPlacementStatus.Placed, (await EditTeamAsync(c, "C", RelayTeamField.Name, "азимут")).Status);
        Assert.Equal((101, 3), await PlaceOfAsync(c, "C"));
        Assert.False((await RelayOfAsync(c, "C"))!.IsIncomplete);    // all three legs taken now

        // A new name makes a new team (no number yet — numbers are handed out later).
        var created = await EditTeamAsync(c, "D", RelayTeamField.Name, "Полісся");
        Assert.Equal(RelayPlacementStatus.Placed, created.Status);
        Assert.True(created.TeamCreated);
        var place = (await RelayOfAsync(c, "D"))!;
        Assert.Null(place.TeamNumber);
        Assert.Equal("Полісся", place.TeamName);
        Assert.Equal(1, place.Leg);
        Assert.True(place.IsIncomplete);
        Assert.Equal(1, place.FilledLegs);

        // A runner alone on their team renames it — nothing else can be meant.
        Assert.Equal(RelayPlacementStatus.TeamChanged, (await EditTeamAsync(c, "D", RelayTeamField.Name, "Беркут")).Status);
        Assert.Equal("Беркут", (await RelayOfAsync(c, "D"))!.TeamName);

        // An empty cell takes the runner off the team.
        Assert.Equal(RelayPlacementStatus.Released, (await EditTeamAsync(c, "D", RelayTeamField.Name, "")).Status);
        Assert.Null(await RelayOfAsync(c, "D"));
    }

    [Fact]
    public async Task ANewNameForATeamWithTeammatesIsAsked()
    {
        var c = await BuildAsync();

        var asked = await EditTeamAsync(c, "A", RelayTeamField.Name, "Беркут");
        Assert.Equal(RelayPlacementStatus.ConfirmTeamChange, asked.Status);
        Assert.Equal(2, asked.MemberCount);
        Assert.True(asked.CanMoveRunner);
        Assert.Equal("Азимут", (await RelayOfAsync(c, "A"))!.TeamName);   // nothing written yet

        // «Перевести в нову команду»: only A moves; 101 keeps its name and B.
        var moved = await EditTeamAsync(c, "A", RelayTeamField.Name, "Беркут", RelayTeamChoice.MoveRunner);
        Assert.Equal(RelayPlacementStatus.Placed, moved.Status);
        Assert.Equal("Беркут", (await RelayOfAsync(c, "A"))!.TeamName);
        Assert.Equal("Азимут", (await RelayOfAsync(c, "B"))!.TeamName);

        // «Перейменувати команду»: every runner of the team sees the new name.
        await EditTeamAsync(c, "C", RelayTeamField.Name, "Азимут");
        var renamed = await EditTeamAsync(c, "B", RelayTeamField.Name, "Полісся", RelayTeamChoice.ChangeTeam);
        Assert.Equal(RelayPlacementStatus.TeamChanged, renamed.Status);
        Assert.Equal("Полісся", (await RelayOfAsync(c, "C"))!.TeamName);
        Assert.Equal(101, (await RelayOfAsync(c, "C"))!.TeamNumber);
    }

    [Fact]
    public async Task ChangingTheTeamNumberIsAlwaysAsked()
    {
        var c = await BuildAsync();
        await c.AddRelayTeamAsync("R", 102, "Компас", 1, ("E", 1));

        // A number no team has: renumber the team, or move only this runner — asked.
        var asked = await EditTeamAsync(c, "A", RelayTeamField.Number, "105");
        Assert.Equal(RelayPlacementStatus.ConfirmTeamChange, asked.Status);
        Assert.Equal("101", asked.TeamLabel);
        Assert.Equal("105", asked.NewValue);
        Assert.Equal(2, asked.MemberCount);
        Assert.Equal((101, 1), await PlaceOfAsync(c, "A"));

        var renumbered = await EditTeamAsync(c, "A", RelayTeamField.Number, "105", RelayTeamChoice.ChangeTeam);
        Assert.Equal(RelayPlacementStatus.TeamChanged, renumbered.Status);
        Assert.Equal((105, 1), await PlaceOfAsync(c, "A"));
        Assert.Equal((105, 2), await PlaceOfAsync(c, "B"));
        Assert.Equal("105.2", (await RelayOfAsync(c, "B"))!.Bib);

        // A number another team has can only mean moving the runner — asked too.
        var taken = await EditTeamAsync(c, "B", RelayTeamField.Number, "102");
        Assert.Equal(RelayPlacementStatus.NumberTaken, taken.Status);
        Assert.Equal("102", taken.OtherTeam);
        Assert.Equal(RelayPlacementStatus.Placed,
            (await EditTeamAsync(c, "B", RelayTeamField.Number, "102", RelayTeamChoice.MoveRunner)).Status);
        Assert.Equal((102, 2), await PlaceOfAsync(c, "B"));

        // «Перевести в нову команду» with a new number: a new team, only this runner.
        Assert.Equal(RelayPlacementStatus.Placed,
            (await EditTeamAsync(c, "B", RelayTeamField.Number, "107", RelayTeamChoice.MoveRunner)).Status);
        Assert.Equal((107, 2), await PlaceOfAsync(c, "B"));
        Assert.Equal((102, 1), await PlaceOfAsync(c, "E"));

        // A cleared number leaves the team without one (asked first).
        Assert.Equal(RelayPlacementStatus.ConfirmTeamChange, (await EditTeamAsync(c, "A", RelayTeamField.Number, "")).Status);
        await EditTeamAsync(c, "A", RelayTeamField.Number, "", RelayTeamChoice.ChangeTeam);
        Assert.Equal((null, 1), await PlaceOfAsync(c, "A"));

        // A runner on no team joins the team with the number, without a question.
        Assert.Equal(RelayPlacementStatus.Placed, (await EditTeamAsync(c, "C", RelayTeamField.Number, "102")).Status);
        Assert.Equal((102, 2), await PlaceOfAsync(c, "C"));
    }
}
