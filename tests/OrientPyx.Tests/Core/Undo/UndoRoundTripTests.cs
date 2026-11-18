using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Tests.Support;
using static OrientPyx.Tests.Core.Undo.UndoKit;

namespace OrientPyx.Tests.Core.Undo;

/// <summary>
/// The main undo test (§11 of docs/undo-redo-plan.md): a few dozen editor operations, each its own undo step, undone
/// in reverse order — the whole <c>event.db</c> is then exactly as before and the results equal the approved ones —
/// and redone in order — the database is exactly as after the edits. Once on a copy of the synthetic competition
/// (participants, chips, groups, statuses, fields, deletes and adds), once on a relay day (legs, «Замінити», team
/// numbers for all / one runner moved, team swaps and deletes, leg courses).
/// </summary>
[Collection(AppCollection.Name)]
public sealed class UndoRoundTripTests
{
    private sealed record Done(string Label, Guid Step);

    [Fact]
    public async Task SyntheticCompetition_EveryEditUndoneAndRedone()
    {
        var env = TestEnvironment.Instance;
        var id = TestIds.Next("undo_synth");
        await SyntheticCompetition.CreateAsync(Sp, id);
        var ev = await env.GetEventAsync(id);
        var folder = ev.FolderPath;
        var days = (await Store.GetDaysAsync(folder)).OrderBy(d => d.Number).ToList();
        var day1 = days[0].Id;
        var day2 = days[1].Id;
        var session = Sp.GetRequiredService<ISessionService>();
        await session.SelectAsync(ev, days[0]);

        // Reads that write (the FSOU discount seeding, the points-rule copy) run once before the first dump: they are
        // system writes outside every step, so they must not show up as a difference later.
        await WarmUpAsync();
        var initial = await EventDbDump.TakeAsync(folder);

        var participants = (await Store.GetParticipantsAsync(folder)).ToDictionary(p => p.Number);
        Guid P(string number) => participants[number].Id;
        var groups = (await Store.GetGroupsAsync(folder)).ToDictionary(g => g.Name);
        var steps = new List<Done>();
        async Task Do(string label, Func<Task> action) => steps.Add(new Done(label, await StepAsync(action)));
        async Task<ParticipantDayRow> RowAsync(Guid participantId)
            => (await Editor.GetParticipantDayRowsAsync()).Single(r => r.ParticipantId == participantId);

        await Do("row edit", async () =>
        {
            var row = await RowAsync(P("101"));
            await Editor.UpdateParticipantDayRowAsync(row with
            {
                FullName = "Петренко Іван Іванович", Rank = "КМС", Coach = "Тренер", Note = "примітка", Representative = "ДЮСШ-1"
            });
        });
        await Do("chip swap", () => Editor.SwapParticipantDayChipsAsync(P("101"), day1, "8001002", "8001001"));
        await Do("group on day 2", () => Editor.SetParticipantDayGroupAsync(P("203"), day2, groups["Ч21"].Id));
        await Do("status (link + read-out)", () => Editor.SetParticipantDayResultStatusAsync(P("103"), day1, FinishStatus.Dnf));
        var field = "";
        await Do("new field", async () =>
            field = (await Editor.AddCustomFieldAsync("Стать", CustomFieldKind.Text, []))!.Key);
        await Do("field value", () => Editor.SetParticipantFieldAsync(P("101"), field, "Ч"));
        await Do("field value of a runner deleted later", () => Editor.SetParticipantFieldAsync(P("202"), field, "Ж"));
        await Do("bonus", () => Editor.SetParticipantDayBonusAsync(P("201"), day1, 3));
        await Do("start time", () => Editor.SetParticipantDayStartTimeAsync(P("201"), day2, new TimeSpan(11, 0, 0)));
        await Do("out of competition", () => Editor.SetParticipantDayOutOfCompetitionAsync(P("203"), day1, true));
        EntryFeeDiscount? discount = null;
        await Do("new discount", async () => discount = await Editor.AddEntryFeeDiscountRowAsync());
        await Do("discount on a runner", () => Editor.SetParticipantDiscountAsync(P("202"), discount!.Id, true));
        await Do("delete participant (two days, a field value, a discount)", () => Editor.DeleteParticipantAsync(P("202")));
        ParticipantDayRow? added = null;
        await Do("add participant", async () => added = await Editor.AddParticipantToDayAsync());
        await Do("fill the added row", () => Editor.UpdateParticipantDayRowAsync(added! with
        {
            FullName = "Новенький Петро", Number = "250", Chip = "8009999"
        }));
        await Do("numbers batch", () => Editor.SetParticipantNumbersBatchAsync(
            [(P("101"), "301"), (P("102"), "302"), (P("103"), "303")]));
        await Do("check-in", () => Editor.SetParticipantsCheckedInAsync([P("101"), P("102"), P("201")], true));
        await Do("new region and its use", async () =>
        {
            var region = (await Editor.AddRegionAsync("Полтавська"))!;
            await Editor.SetParticipantRegionAsync(P("101"), region.Id);
        });
        await Do("new club and its use", async () =>
        {
            var club = (await Editor.AddClubAsync("Азимут"))!;
            await Editor.SetParticipantClubAsync(P("102"), club.Id);
        });
        await Do("day payment", () => Editor.SetParticipantDayPaymentAsync(P("102"), day1, "200"));
        await Do("discount on another runner", () => Editor.SetParticipantDiscountAsync(P("103"), discount!.Id, true));
        await Do("raised fee", () => Editor.SetParticipantPaysRaisedFeeAsync(P("201"), true));
        await Do("chip taken from another runner", () => Editor.ReassignParticipantDayChipAsync(P("203"), day2, "8002001"));
        await Do("leave day 2", async () =>
        {
            var link = (await Store.GetParticipantDaysAsync(folder, day2)).Single(l => l.ParticipantId == P("103"));
            await Editor.RemoveParticipantFromDayAsync(link.Id, P("103"));
        });
        await Do("delete the field (and its values)", () => Editor.DeleteCustomFieldAsync(field));
        await Do("rental chip", async () =>
        {
            var chip = await Editor.AddRentalChipAsync();
            chip.Number = "8001001";
            chip.Note = "SIAC";
            await Editor.UpdateRentalChipAsync(chip);
        });
        await Do("group course and distance", async () =>
        {
            var row = (await Editor.GetGroupDayRowsAsync()).Single(r => r.GroupId == groups["Ж21"].Id);
            await Editor.UpdateGroupDayRowAsync(row with { CourseOrder = "31 32 33 34", DistanceKm = 2.9m });
        });
        await Do("group entry fee", () => Editor.UpdateGroupEntryFeeAsync(groups["Ч21"].Id, 150m));
        await Do("rename a group onto another one (merge)", async () =>
        {
            var row = (await Editor.GetGroupDayRowsAsync()).Single(r => r.GroupId == groups["Ж21"].Id);
            Assert.True(await Editor.UpdateGroupDayRowAsync(row with { Name = "Ч21" }));
        });

        var final = await EventDbDump.TakeAsync(folder);
        Assert.NotEqual(initial, final);

        // Undo everything, newest first.
        var reverts = new List<Done>();
        foreach (var step in Enumerable.Reverse(steps))
            reverts.Add(new Done(step.Label, await RevertExpectAsync(step.Step, UndoRevertStatus.Done, $"undo «{step.Label}»")));

        Assert.Equal(initial, await EventDbDump.TakeAsync(folder));
        Assert.Equal(await File.ReadAllTextAsync(env.GoldenPath(env.SyntheticId)),
            await ResultsSnapshot.BuildAsync(Editor, session, days));

        // Redo everything, oldest first (the last undo is the first redo).
        await session.SetCurrentDayAsync(days[0]);
        foreach (var revert in Enumerable.Reverse(reverts))
            await RevertExpectAsync(revert.Step, UndoRevertStatus.Done, $"redo «{revert.Label}»");

        Assert.Equal(final, await EventDbDump.TakeAsync(folder));
    }

    [Fact]
    public async Task RelayDay_EveryEditUndoneAndRedone()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("undo_relay"), discipline: DisciplineId.Relay);
        await c.AddControlsAsync(1, "31", "32", "33");
        await c.AddGroupAsync("R", "31 32 33", s => s.LegCount = 3);
        await c.AddGroupAsync("R2", "31 33", s => s.LegCount = 3);
        var chip = 5001;
        foreach (var name in new[] { "A", "B", "C", "D", "E", "F", "G" })
            await c.AddRunnerAsync(name, "", (chip++).ToString(), "R", CompetitionBuilder.At(10, 0));
        await c.SetRelaySettingsAsync("R", 1, 3, CompetitionBuilder.At(10, 0));
        await c.AddRelayTeamAsync("R", 101, "Азимут", 1, ("A", 1), ("B", 2));
        await c.AddRelayTeamAsync("R", 102, "Вітер", 1, ("C", 1));
        await c.AddRelayTeamAsync("R", 103, "Компас", 1, ("F", 1));
        await c.AddReadoutAsync(1, "5001", "31 32 33", null, CompetitionBuilder.At(10, 20));
        await c.AddReadoutAsync(1, "5002", "31 32 33", null, CompetitionBuilder.At(10, 41));
        await c.SelectAsync();
        var day = c.Day(1).Id;
        Guid R(string name) => c.Runners[name].Id;
        async Task<Guid> TeamAsync(int number)
            => (await Store.GetRelayTeamsAsync(c.Folder, day)).Single(t => t.Number == number).Id;
        Task<RelayPlacementOutcome> Place(string runner, int? team, int? leg, bool replace = false)
            => Editor.PlaceRelayRunnerAsync(new RelayPlacementRequest(R(runner), day, team, leg, replace));
        Task<RelayPlacementOutcome> TeamCell(string runner, RelayTeamField field, string value, RelayTeamChoice choice)
            => Editor.EditRunnerRelayTeamAsync(new RelayTeamCellEdit(R(runner), day, field, value, choice));

        await WarmUpAsync();
        var initial = await EventDbDump.TakeAsync(c.Folder);
        var results = await c.SnapshotAsync();

        var steps = new List<Done>();
        async Task Do(string label, Func<Task> action) => steps.Add(new Done(label, await StepAsync(action)));
        async Task DoPlaced(string label, Func<Task<RelayPlacementOutcome>> action, RelayPlacementStatus expected)
            => await Do(label, async () => Assert.Equal(expected, (await action()).Status));

        await DoPlaced("place on a free leg", () => Place("D", 101, 3), RelayPlacementStatus.Placed);
        await DoPlaced("«Замінити» the leg's holder", () => Place("E", 101, 1, replace: true), RelayPlacementStatus.Placed);
        await DoPlaced("team number for all", () => TeamCell("B", RelayTeamField.Number, "105", RelayTeamChoice.ChangeTeam),
            RelayPlacementStatus.TeamChanged);
        await DoPlaced("move one runner to a new number", () => TeamCell("C", RelayTeamField.Number, "107", RelayTeamChoice.MoveRunner),
            RelayPlacementStatus.Placed);
        await Do("swap two teams' numbers", async () =>
        {
            var t105 = await TeamAsync(105);
            var t103 = await TeamAsync(103);
            Assert.True(await Editor.SetRelayTeamNumbersAsync([new RelayTeamNumberChange(t105, 103), new RelayTeamNumberChange(t103, 105)]));
        });
        await DoPlaced("swap legs with a teammate", () => Place("E", 103, 2), RelayPlacementStatus.Swapped);
        await DoPlaced("rename a team from its runner's cell", () => TeamCell("F", RelayTeamField.Name, "Беркут", RelayTeamChoice.ChangeTeam),
            RelayPlacementStatus.TeamChanged);
        await Do("delete a team with a runner", async () => await Editor.DeleteRelayTeamAsync(await TeamAsync(107)));
        await Do("create teams", () => Editor.CreateRelayTeamsAsync(c.Groups["R"].Id, 2, 200));
        await Do("edit a team", async () =>
        {
            var team = (await Store.GetRelayTeamsAsync(c.Folder, day)).Single(t => t.Number == 200);
            Assert.True(await Editor.UpdateRelayTeamAsync(new RelayTeamEdit(team.Id, team.GroupId, 210, "Нова", null, null, true, null)));
        });
        await Do("assign a leg", async () => Assert.True(await Editor.AssignRelayLegAsync(await TeamAsync(210), 1, R("A"))));
        await DoPlaced("the whole team into another group",
            () => Editor.ChangeRelayRunnerGroupAsync(new RelayGroupEdit(R("A"), day, c.Groups["R2"].Id, RelayTeamChoice.ChangeTeam)),
            RelayPlacementStatus.TeamChanged);
        await Do("leg courses", () => Editor.SetLegCoursesAsync([new LegCourseEntry(103, 1, "A"), new LegCourseEntry(103, 2, "B")]));
        await Do("leg courses replaced for the group", () => Store.ReplaceGroupLegCourseAssignmentsAsync(c.Folder, day, c.Groups["R"].Id,
        [
            new LegCourseAssignment { EventDayId = day, GroupId = c.Groups["R"].Id, TeamNumber = 103, Leg = 1, CourseKey = "B" },
            new LegCourseAssignment { EventDayId = day, GroupId = c.Groups["R"].Id, TeamNumber = 103, Leg = 2, CourseKey = "A" },
        ]));
        await DoPlaced("take a runner off the team", () => Place("D", null, null), RelayPlacementStatus.Released);
        await DoPlaced("a new team by a runner's number cell", () => Place("G", 301, 2), RelayPlacementStatus.Placed);
        await Do("delete a runner who holds a leg", () => Editor.DeleteParticipantAsync(R("B")));

        var final = await EventDbDump.TakeAsync(c.Folder);
        Assert.NotEqual(initial, final);

        var reverts = new List<Done>();
        foreach (var step in Enumerable.Reverse(steps))
            reverts.Add(new Done(step.Label, await RevertExpectAsync(step.Step, UndoRevertStatus.Done, $"undo «{step.Label}»")));

        Assert.Equal(initial, await EventDbDump.TakeAsync(c.Folder));
        Assert.Equal(results, await c.SnapshotAsync());

        foreach (var revert in Enumerable.Reverse(reverts))
            await RevertExpectAsync(revert.Step, UndoRevertStatus.Done, $"redo «{revert.Label}»");

        Assert.Equal(final, await EventDbDump.TakeAsync(c.Folder));
    }

    // Every read a page makes on opening, so the writes some of them make by themselves happen before the first dump.
    private static async Task WarmUpAsync()
    {
        await Editor.GetParticipantDayRowsAsync();
        await Editor.GetParticipantRosterAsync();
        await Editor.GetEntryFeeDiscountsAsync();
        await Editor.GetCustomFieldsAsync();
        await Editor.GetGroupDayRowsAsync();
        await Editor.GetRelayTeamsDataAsync();
    }
}
