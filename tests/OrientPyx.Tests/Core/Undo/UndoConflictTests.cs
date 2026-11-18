using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Tests.Support;
using static OrientPyx.Tests.Core.Undo.UndoKit;

namespace OrientPyx.Tests.Core.Undo;

/// <summary>
/// A revert never overwrites what changed after the step (§6.6, У-7…У-11): a third value, a deleted row, a value now
/// held by another row (look-alike letters included), a missing or still-needed reference and a closed day all leave
/// the database untouched; a value already put back is skipped, not a conflict; swaps and replacements whose values
/// the revert itself frees revert without false conflicts; the points-rule copy (never journalled) is recomputed.
/// </summary>
[Collection(AppCollection.Name)]
public sealed class UndoConflictTests
{
    private static readonly TimeSpan Ten = CompetitionBuilder.At(10, 0);

    // Group Ч21 on every day, three runners with numbers and chips.
    private static async Task<CompetitionBuilder> BuildAsync(int days = 1, string firstNumber = "1")
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("undo_conf"), dayCount: days);
        await c.AddControlsAsync(1, "31", "32");
        await c.AddGroupAsync("Ч21", "31 32");
        await c.AddRunnerAsync("Іваненко Іван", firstNumber, "1001", "Ч21", Ten);
        await c.AddRunnerAsync("Петренко Петро", "2", "1002", "Ч21", Ten);
        await c.AddRunnerAsync("Коваль Олег", "3", "1003", "Ч21", Ten);
        await c.SelectAsync();
        return c;
    }

    // One relay day: group R with 3 legs, runners A…E; team 101 with A on leg 1 and B on leg 2, team 102 with C on leg 1.
    private static async Task<CompetitionBuilder> BuildRelayAsync()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("undo_confr"), discipline: DisciplineId.Relay);
        await c.AddControlsAsync(1, "31", "32");
        await c.AddGroupAsync("R", "31 32", s => s.LegCount = 3);
        foreach (var name in new[] { "A", "B", "C", "D", "E" })
            await c.AddRunnerAsync(name, "", "", "R");
        await c.AddRelayTeamAsync("R", 101, "Азимут", 1, ("A", 1), ("B", 2));
        await c.AddRelayTeamAsync("R", 102, "Вітер", 1, ("C", 1));
        await c.SelectAsync();
        return c;
    }

    private static Guid Id(CompetitionBuilder c, string runner) => c.Runners[runner].Id;

    private static Task<RelayPlacementOutcome> Place(CompetitionBuilder c, string runner, int? team, int? leg, bool replace = false)
        => Editor.PlaceRelayRunnerAsync(new RelayPlacementRequest(Id(c, runner), c.Day(1).Id, team, leg, replace));

    private static async Task<(int? Team, int? Leg)> PlaceOfAsync(CompetitionBuilder c, string runner)
    {
        var link = await c.GetLinkAsync(runner, 1);
        var team = link.RelayTeamId is { } teamId
            ? (await Store.GetRelayTeamsAsync(c.Folder, c.Day(1).Id)).Single(t => t.Id == teamId).Number
            : null;
        return (team, link.Leg);
    }

    private static async Task<UndoRevertResult> RevertUnchangedAsync(CompetitionBuilder c, Guid step, UndoRevertStatus expected)
    {
        var before = await EventDbDump.TakeAsync(c.Folder);
        var result = (await RevertAsync(step)).Result;
        Assert.True(result.Status == expected, $"expected {expected}, got {result.Status} {Describe(result)}");
        Assert.Equal(before, await EventDbDump.TakeAsync(c.Folder));
        return result;
    }

    private static Task SetChipAsync(CompetitionBuilder c, string runner, string chip, int day = 1)
        => Editor.SetParticipantDayChipAsync(Id(c, runner), c.Day(day).Id, chip);

    // ── Values changed after the step

    [Fact]
    public async Task AThirdValueIsAConflictAndNothingChanges()
    {
        var c = await BuildAsync();
        var step = await StepAsync(async () =>
        {
            var row = (await Editor.GetParticipantDayRowsAsync()).Single(r => r.ParticipantId == Id(c, "Іваненко Іван"));
            await Editor.UpdateParticipantDayRowAsync(row with { FullName = "Іваненко Іван Іванович", Chip = "2001" });
        });
        await SetChipAsync(c, "Іваненко Іван", "3001");   // another station, outside the step

        var result = await RevertUnchangedAsync(c, step, UndoRevertStatus.Conflict);

        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal(UndoConflictKind.Changed, conflict.Kind);
        Assert.Equal(nameof(ParticipantDay), conflict.Entity);
        Assert.Equal(nameof(ParticipantDay.Chip), conflict.Field);
        Assert.Equal("2001", conflict.Expected);
        Assert.Equal("3001", conflict.Actual);
        Assert.StartsWith("Іваненко Іван", conflict.Row);
        // All or nothing: the name the step changed stays as the step left it.
        Assert.Equal("Іваненко Іван Іванович",
            (await Store.GetParticipantsAsync(c.Folder)).Single(p => p.Id == Id(c, "Іваненко Іван")).FullName);
    }

    [Fact]
    public async Task AnotherFieldOfTheSameRowChangedSinceIsKept()
    {
        var c = await BuildAsync();
        var runner = Id(c, "Іваненко Іван");
        var step = await StepAsync(() => SetChipAsync(c, "Іваненко Іван", "2001"));
        await Editor.SetParticipantDayStartTimeAsync(runner, c.Day(1).Id, CompetitionBuilder.At(11, 30));
        await Editor.SetParticipantDayBonusAsync(runner, c.Day(1).Id, 4);

        await RevertExpectAsync(step, UndoRevertStatus.Done);

        var link = await c.GetLinkAsync("Іваненко Іван", 1);
        Assert.Equal("1001", link.Chip);
        Assert.Equal(CompetitionBuilder.At(11, 30), link.StartTime);
        Assert.Equal(4, link.Bonus);
    }

    [Fact]
    public async Task EverythingPutBackOutsideTheStepIsAlreadyReverted()
    {
        var c = await BuildAsync();
        var step = await StepAsync(() => SetChipAsync(c, "Іваненко Іван", "2001"));
        await SetChipAsync(c, "Іваненко Іван", "1001");

        await RevertUnchangedAsync(c, step, UndoRevertStatus.AlreadyReverted);
    }

    [Fact]
    public async Task APartPutBackOutsideTheStepRevertsTheRestAndRedoRepeatsOnlyThat()
    {
        var c = await BuildAsync();
        var step = await StepAsync(async () =>
        {
            var row = (await Editor.GetParticipantDayRowsAsync()).Single(r => r.ParticipantId == Id(c, "Іваненко Іван"));
            await Editor.UpdateParticipantDayRowAsync(row with { FullName = "Іваненко Іван Іванович", Chip = "2001" });
        });
        await SetChipAsync(c, "Іваненко Іван", "1001");
        async Task<string> NameAsync() => (await Store.GetParticipantsAsync(c.Folder)).Single(p => p.Id == Id(c, "Іваненко Іван")).FullName;

        var outcome = await RevertAsync(step);
        Assert.Equal(UndoRevertStatus.Done, outcome.Result.Status);
        Assert.Equal(1, outcome.Result.ChangedRows);
        Assert.Equal("Іваненко Іван", await NameAsync());
        Assert.Equal("1001", (await c.GetLinkAsync("Іваненко Іван", 1)).Chip);

        // Redo repeats what the revert changed — the name — not the whole original edit.
        await RevertExpectAsync(outcome.RevertStepId, UndoRevertStatus.Done, "redo");
        Assert.Equal("Іваненко Іван Іванович", await NameAsync());
        Assert.Equal("1001", (await c.GetLinkAsync("Іваненко Іван", 1)).Chip);
    }

    [Fact]
    public async Task ADnsTheReadOutClearedIsAlreadyRevertedAndTheReadOutStays()
    {
        var c = await BuildAsync();
        var step = await StepAsync(() => Editor.SetParticipantDayResultStatusAsync(Id(c, "Іваненко Іван"), c.Day(1).Id, FinishStatus.Dns));
        Assert.Equal(FinishStatus.Dns, (await c.GetLinkAsync("Іваненко Іван", 1)).ResultStatusOverride);

        // The finish auto-read (a system write, outside every step) reads the chip and clears the manual DNS.
        var read = await Editor.ImportFinishReadoutsAsync(new ChipReadData
        {
            Records =
            [
                new ChipReadRecord
                {
                    ChipNumber = "1001",
                    StartTime = c.Date + Ten,
                    FinishTime = c.Date + CompetitionBuilder.At(10, 25),
                    Punches = [new ChipPunch("31", c.Date + CompetitionBuilder.At(10, 10)), new ChipPunch("32", c.Date + CompetitionBuilder.At(10, 20))],
                    ReadMark = "1",
                },
            ],
        });
        Assert.Equal(1, read.Added);
        Assert.Null((await c.GetLinkAsync("Іваненко Іван", 1)).ResultStatusOverride);

        await RevertUnchangedAsync(c, step, UndoRevertStatus.AlreadyReverted);
        Assert.Single(await Store.GetFinishReadoutsAsync(c.Folder, c.Day(1).Id));
    }

    // ── Values taken by other rows after the step

    [Fact]
    public async Task ANumberTakenByALookAlikeIsAConflict()
    {
        var c = await BuildAsync(firstNumber: "M12");                                 // Latin M
        var step = await StepAsync(() => Editor.SetParticipantNumbersBatchAsync([(Id(c, "Іваненко Іван"), "5")]));
        await Editor.SetParticipantNumbersBatchAsync([(Id(c, "Петренко Петро"), "М12")]);   // Cyrillic М

        var result = await RevertUnchangedAsync(c, step, UndoRevertStatus.Conflict);

        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal(UndoConflictKind.ValueTaken, conflict.Kind);
        Assert.Equal(nameof(Participant), conflict.Entity);
        Assert.Equal(nameof(Participant.Number), conflict.Field);
        Assert.Equal("M12", conflict.Expected);
        Assert.Equal("Петренко Петро", conflict.Actual);
    }

    [Fact]
    public async Task AChipTakenOnTheDayIsAConflict()
    {
        var c = await BuildAsync();
        var step = await StepAsync(() => SetChipAsync(c, "Іваненко Іван", "2001"));
        await SetChipAsync(c, "Петренко Петро", "1001");

        var result = await RevertUnchangedAsync(c, step, UndoRevertStatus.Conflict);

        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal(UndoConflictKind.ValueTaken, conflict.Kind);
        Assert.Equal(nameof(ParticipantDay.Chip), conflict.Field);
        Assert.Equal("1001", conflict.Expected);
    }

    [Fact]
    public async Task ARegionNameTakenInAnotherCaseIsAConflict()
    {
        var c = await BuildAsync();
        var region = (await Editor.AddRegionAsync("Київ"))!;
        var step = await StepAsync(() => Editor.UpdateRegionAsync(new Region { Id = region.Id, Name = "Львів" }));
        Assert.NotNull(await Editor.AddRegionAsync("київ"));                    // free now that the first one is «Львів»

        var result = await RevertUnchangedAsync(c, step, UndoRevertStatus.Conflict);

        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal(UndoConflictKind.ValueTaken, conflict.Kind);
        Assert.Equal(nameof(Region), conflict.Entity);
        Assert.Equal(nameof(Region.Name), conflict.Field);
    }

    [Fact]
    public async Task ALegTakenAfterTheStepIsAConflict()
    {
        var c = await BuildRelayAsync();
        var step = await StepAsync(async () => Assert.Equal(RelayPlacementStatus.Released, (await Place(c, "A", null, null)).Status));
        Assert.Equal(RelayPlacementStatus.Placed, (await Place(c, "D", 101, 1)).Status);

        var result = await RevertUnchangedAsync(c, step, UndoRevertStatus.Conflict);

        Assert.NotEmpty(result.Conflicts);
        Assert.Equal((101, 1), await PlaceOfAsync(c, "D"));
        Assert.Equal((null, null), await PlaceOfAsync(c, "A"));
    }

    [Fact]
    public async Task ATeamNumberTakenAfterTheStepIsAConflict()
    {
        var c = await BuildRelayAsync();
        var team = (await Store.GetRelayTeamsAsync(c.Folder, c.Day(1).Id)).Single(t => t.Number == 101);
        var step = await StepAsync(async () => Assert.True(await Editor.UpdateRelayTeamAsync(
            new RelayTeamEdit(team.Id, team.GroupId, 105, team.Name, null, null, false, null))));
        Assert.Equal(1, await Editor.CreateRelayTeamsAsync(c.Groups["R"].Id, 1, 101));

        var result = await RevertUnchangedAsync(c, step, UndoRevertStatus.Conflict);

        Assert.NotEmpty(result.Conflicts);
        Assert.Contains((await Store.GetRelayTeamsAsync(c.Folder, c.Day(1).Id)), t => t.Id == team.Id && t.Number == 105);
    }

    // ── Swaps and replacements: the values the revert frees are free

    [Fact]
    public async Task SwappedChipsRevertAndRedo()
    {
        var c = await BuildAsync();
        var step = await StepAsync(() => Editor.SwapParticipantDayChipsAsync(Id(c, "Іваненко Іван"), c.Day(1).Id, "1002", "1001"));
        Assert.Equal("1002", (await c.GetLinkAsync("Іваненко Іван", 1)).Chip);

        var redo = await RevertExpectAsync(step, UndoRevertStatus.Done);
        Assert.Equal("1001", (await c.GetLinkAsync("Іваненко Іван", 1)).Chip);
        Assert.Equal("1002", (await c.GetLinkAsync("Петренко Петро", 1)).Chip);

        var undoAgain = await RevertExpectAsync(redo, UndoRevertStatus.Done, "redo");
        Assert.Equal("1002", (await c.GetLinkAsync("Іваненко Іван", 1)).Chip);
        Assert.Equal("1001", (await c.GetLinkAsync("Петренко Петро", 1)).Chip);

        // Undo and redo again: every revert is a step of its own, reverted in turn.
        var redoAgain = await RevertExpectAsync(undoAgain, UndoRevertStatus.Done, "undo again");
        Assert.Equal("1001", (await c.GetLinkAsync("Іваненко Іван", 1)).Chip);
        await RevertExpectAsync(redoAgain, UndoRevertStatus.Done, "redo again");
        Assert.Equal("1002", (await c.GetLinkAsync("Іваненко Іван", 1)).Chip);
    }

    [Fact]
    public async Task SwappedTeamNumbersRevertAndRedo()
    {
        var c = await BuildRelayAsync();
        var teams = (await Store.GetRelayTeamsAsync(c.Folder, c.Day(1).Id)).ToDictionary(t => t.Number!.Value, t => t.Id);
        var step = await StepAsync(async () => Assert.True(await Editor.SetRelayTeamNumbersAsync(
            [new RelayTeamNumberChange(teams[101], 102), new RelayTeamNumberChange(teams[102], 101)])));
        Assert.Equal((102, 1), await PlaceOfAsync(c, "A"));

        var redo = await RevertExpectAsync(step, UndoRevertStatus.Done);
        Assert.Equal((101, 1), await PlaceOfAsync(c, "A"));
        Assert.Equal((102, 1), await PlaceOfAsync(c, "C"));

        await RevertExpectAsync(redo, UndoRevertStatus.Done, "redo");
        Assert.Equal((102, 1), await PlaceOfAsync(c, "A"));
        Assert.Equal((101, 1), await PlaceOfAsync(c, "C"));
    }

    [Fact]
    public async Task AReplacedLegHolderComesBackAndRedoReplacesAgain()
    {
        var c = await BuildRelayAsync();
        var step = await StepAsync(async () => Assert.Equal(RelayPlacementStatus.Placed, (await Place(c, "C", 101, 1, replace: true)).Status));
        Assert.Equal((101, 1), await PlaceOfAsync(c, "C"));
        Assert.Equal((null, null), await PlaceOfAsync(c, "A"));

        var redo = await RevertExpectAsync(step, UndoRevertStatus.Done);
        Assert.Equal((101, 1), await PlaceOfAsync(c, "A"));
        Assert.Equal((102, 1), await PlaceOfAsync(c, "C"));

        await RevertExpectAsync(redo, UndoRevertStatus.Done, "redo");
        Assert.Equal((101, 1), await PlaceOfAsync(c, "C"));
        Assert.Equal((null, null), await PlaceOfAsync(c, "A"));
    }

    [Fact]
    public async Task ReplacedLegCourseRowsRevertToTheOriginalRows()
    {
        var c = await BuildRelayAsync();
        var day = c.Day(1).Id;
        var group = c.Groups["R"].Id;
        LegCourseAssignment Row(int leg, string course) => new() { EventDayId = day, GroupId = group, TeamNumber = 101, Leg = leg, CourseKey = course };
        await Store.ReplaceGroupLegCourseAssignmentsAsync(c.Folder, day, group, [Row(1, "A"), Row(2, "B")]);
        var original = await EventDbDump.RowsAsync(c.Folder, "LegCourseAssignments");

        // New rows (new ids) under the same (number, leg) keys as the ones deleted: the unique index must never see both.
        var step = await StepAsync(() => Store.ReplaceGroupLegCourseAssignmentsAsync(c.Folder, day, group, [Row(1, "B"), Row(2, "A")]));
        var replaced = await EventDbDump.RowsAsync(c.Folder, "LegCourseAssignments");

        var redo = await RevertExpectAsync(step, UndoRevertStatus.Done);
        Assert.Equal(original, await EventDbDump.RowsAsync(c.Folder, "LegCourseAssignments"));

        await RevertExpectAsync(redo, UndoRevertStatus.Done, "redo");
        Assert.Equal(replaced, await EventDbDump.RowsAsync(c.Folder, "LegCourseAssignments"));
    }

    // ── References

    [Fact]
    public async Task ADeletedParticipantWhoseDayWasDeletedIsAMissingReference()
    {
        var c = await BuildAsync(days: 2);
        var step = await StepAsync(() => Editor.DeleteParticipantAsync(Id(c, "Іваненко Іван")));
        await Editor.DeleteDayAsync(c.Day(2).Id);                            // a bulk write, never a step

        var result = await RevertUnchangedAsync(c, step, UndoRevertStatus.Conflict);

        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal(UndoConflictKind.MissingReference, conflict.Kind);
        Assert.Equal(nameof(ParticipantDay), conflict.Entity);
        Assert.Equal(nameof(ParticipantDay.EventDayId), conflict.Field);
        Assert.Equal(nameof(EventDay), conflict.Expected);
        Assert.Equal("Іваненко Іван", conflict.Row);
    }

    [Fact]
    public async Task ARunnerMovedOutOfAGroupDeletedSinceIsAMissingReference()
    {
        var c = await BuildAsync();
        var g1 = await c.AddGroupAsync("G1", "31");
        var g2 = await c.AddGroupAsync("G2", "32");
        await c.UpdateLinkAsync("Коваль Олег", 1, l => l.GroupId = g1.Id);
        var step = await StepAsync(() => Editor.SetParticipantDayGroupAsync(Id(c, "Коваль Олег"), c.Day(1).Id, g2.Id));
        var settings = await c.GetGroupSettingsAsync("G1", 1);
        await Editor.RemoveGroupFromDayAsync(settings.Id, g1.Id);           // G1 runs on no day now — deleted
        Assert.DoesNotContain(await Store.GetGroupsAsync(c.Folder), g => g.Id == g1.Id);

        var result = await RevertUnchangedAsync(c, step, UndoRevertStatus.Conflict);

        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal(UndoConflictKind.MissingReference, conflict.Kind);
        Assert.Equal(nameof(ParticipantDay.GroupId), conflict.Field);
        Assert.Equal(nameof(Group), conflict.Expected);
    }

    [Fact]
    public async Task ARegionAddedInTheStepAndUsedSinceIsStillReferenced()
    {
        var c = await BuildAsync();
        Region? region = null;
        var step = await StepAsync(async () =>
        {
            region = await Editor.AddRegionAsync("Нова");                  // «+ новий» in the roster
            await Editor.SetParticipantRegionAsync(Id(c, "Іваненко Іван"), region!.Id);
        });
        await Editor.SetParticipantRegionAsync(Id(c, "Петренко Петро"), region!.Id);
        await Editor.SetParticipantRegionAsync(Id(c, "Коваль Олег"), region.Id);

        var result = await RevertUnchangedAsync(c, step, UndoRevertStatus.Conflict);

        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal(UndoConflictKind.StillReferenced, conflict.Kind);
        Assert.Equal(nameof(Region), conflict.Entity);
        Assert.Equal("Нова", conflict.Row);
        Assert.Equal(nameof(Participant), conflict.Field);
        Assert.Equal("2", conflict.Actual);
    }

    [Fact]
    public async Task ATeamCreatedInTheStepAndFilledSinceIsStillReferenced()
    {
        var c = await BuildRelayAsync();
        var step = await StepAsync(async () => Assert.True((await Place(c, "D", 205, 1)).TeamCreated));
        Assert.Equal(RelayPlacementStatus.Placed, (await Place(c, "E", 205, 2)).Status);

        var result = await RevertUnchangedAsync(c, step, UndoRevertStatus.Conflict);

        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal(UndoConflictKind.StillReferenced, conflict.Kind);
        Assert.Equal(nameof(RelayTeam), conflict.Entity);
        Assert.Equal("205", conflict.Row);
        Assert.Equal(nameof(ParticipantDay), conflict.Field);
    }

    [Fact]
    public async Task ARowTheStepChangedAndThatWasDeletedSinceIsAConflict()
    {
        var c = await BuildAsync();
        var step = await StepAsync(() => SetChipAsync(c, "Іваненко Іван", "2001"));
        await Editor.DeleteParticipantAsync(Id(c, "Іваненко Іван"));

        var result = await RevertUnchangedAsync(c, step, UndoRevertStatus.Conflict);

        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal(UndoConflictKind.RowDeleted, conflict.Kind);
        Assert.Equal(nameof(ParticipantDay), conflict.Entity);
    }

    // ── Closed days

    [Fact]
    public async Task ARosterStepOverTwoDaysWaitsForItsClosedDay()
    {
        var c = await BuildAsync(days: 2);
        var runner = Id(c, "Іваненко Іван");
        var step = await StepAsync(() => Editor.SetParticipantDayChipsBatchAsync(
            [(runner, c.Day(1).Id, "2001"), (runner, c.Day(2).Id, "2002")]));
        await Editor.SetDayLockedAsync(c.Day(2).Id, true);

        var locked = await RevertUnchangedAsync(c, step, UndoRevertStatus.DayLocked);
        Assert.Equal(2, locked.LockedDayNumber);

        // The step stays undoable: once the day is open again it reverts.
        await Editor.SetDayLockedAsync(c.Day(2).Id, false);
        await RevertExpectAsync(step, UndoRevertStatus.Done);
        Assert.Equal("1001", (await c.GetLinkAsync("Іваненко Іван", 1)).Chip);
        Assert.Equal("1001", (await c.GetLinkAsync("Іваненко Іван", 2)).Chip);
    }

    // ── The points-rule copy (derived, never journalled)

    [Fact]
    public async Task AGroupRuleChoiceRevertsAfterTheCopyChangedAndTheCopyFollows()
    {
        var app = Sp.GetRequiredService<IAppStore>();
        var rule = await app.AddPointsRuleAsync(PointsRuleKind.Table);
        rule.Name = "undo table";
        rule.TableJson = "[10,8]";
        await app.UpdatePointsRuleAsync(rule);
        var c = await BuildAsync();
        async Task<IReadOnlyList<PointsRule>> CopiesAsync()
            => PointsRuleCopies.Parse((await Store.GetCompetitionInfoAsync(c.Folder))!.PointsRulesJson);

        var step = await StepAsync(async () =>
        {
            var row = Assert.Single(await Editor.GetGroupDayRowsAsync());
            await Editor.UpdateGroupDayRowAsync(row with { PointsRuleId = rule.Id });
        });
        Assert.Equal("[10,8]", Assert.Single(await CopiesAsync()).TableJson);

        // The rule is edited on «Очки» and the copy refreshed — outside the step.
        rule.TableJson = "[20,15]";
        await app.UpdatePointsRuleAsync(rule);
        await Editor.RefreshPointsRuleCopiesAsync();
        Assert.Equal("[20,15]", Assert.Single(await CopiesAsync()).TableJson);

        var redo = await RevertExpectAsync(step, UndoRevertStatus.Done);
        Assert.Null((await c.GetGroupSettingsAsync("Ч21", 1)).PointsRuleId);
        Assert.Empty(await CopiesAsync());

        await RevertExpectAsync(redo, UndoRevertStatus.Done, "redo");
        Assert.Equal(rule.Id, (await c.GetGroupSettingsAsync("Ч21", 1)).PointsRuleId);
        Assert.Equal("[20,15]", Assert.Single(await CopiesAsync()).TableJson);
    }
}
