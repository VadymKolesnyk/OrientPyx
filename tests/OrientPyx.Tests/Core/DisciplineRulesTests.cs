using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Tests.Support;

namespace OrientPyx.Tests.Core;

/// <summary>
/// The judging rules of every built-in discipline, each on a competition built for the case: statuses, times,
/// places, scores, penalties, overrides, out-of-competition, variants and patterns. Everything goes through
/// the real store and <see cref="ICompetitionEditorService.GetDayResultsByParticipantAsync"/>, exactly like the pages.
/// </summary>
[Collection(AppCollection.Name)]
public sealed class DisciplineRulesTests
{
    private static IServiceProvider Sp => TestEnvironment.Instance.Services;

    private static TimeSpan T(int h, int m, int s = 0) => new(h, m, s);

    /// <summary>The readout-log row of a chip on the selected day.</summary>
    private static async Task<FinishReadoutRow> RowAsync(string chip)
    {
        var rows = await Sp.GetRequiredService<ICompetitionEditorService>().GetFinishReadoutRowsAsync();
        return rows.Single(x => x.ChipNumber == chip);
    }

    private static async Task<string> DetailAsync(string chip) => (await RowAsync(chip)).StatusDetail;

    // ---- Set course ----------------------------------------------------------------------------------------

    [Fact]
    public async Task SetCourse_StatusesTimesAndPlaces()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("sc"));
        await c.AddControlsAsync(1, "S1", "31", "32", "33", "F1");
        await c.AddGroupAsync("Ч21", "S1 31 32 33 F1");
        await c.AddRunnerAsync("Ok", "1", "1001", "Ч21", T(10, 0));
        await c.AddRunnerAsync("Extra", "2", "1002", "Ч21", T(10, 1));
        await c.AddRunnerAsync("WrongOrder", "3", "1003", "Ч21", T(10, 2));
        await c.AddRunnerAsync("Missing", "4", "1004", "Ч21", T(10, 3));
        await c.AddRunnerAsync("NoFinish", "5", "1005", "Ч21", T(10, 4));
        await c.AddRunnerAsync("NotRead", "6", "1006", "Ч21", T(10, 5));
        await c.AddRunnerAsync("Tie", "7", "1007", "Ч21", T(10, 6));

        await c.AddReadoutAsync(1, "1001", "S1 31 32 33 F1", T(10, 0), T(10, 30));
        await c.AddReadoutAsync(1, "1002", "31 99 32 33 40", T(10, 1), T(10, 29));
        await c.AddReadoutAsync(1, "1003", "31 33 32", T(10, 2), T(10, 20));
        await c.AddReadoutAsync(1, "1004", "31 33", T(10, 3), T(10, 20));
        var dnf = await c.AddReadoutAsync(1, "1005", "31 32 33", T(10, 4), T(10, 40));
        dnf.FinishTime = null;
        await Sp.GetRequiredService<IEventStore>().UpdateFinishReadoutAsync(c.Folder, dnf);
        await c.AddReadoutAsync(1, "1007", "31 32 33", T(10, 6), T(10, 36));

        var r = await c.ResultsAsync();
        Assert.Equal(FinishStatus.Ok, r["Ok"].Status);
        Assert.Equal(T(0, 30), r["Ok"].ResultTime);
        Assert.Equal(FinishStatus.Ok, r["Extra"].Status);
        Assert.Equal(T(0, 28), r["Extra"].ResultTime);
        Assert.Equal(FinishStatus.Mp, r["WrongOrder"].Status);
        Assert.Single(r["WrongOrder"].Missed);
        Assert.Equal("33", await DetailAsync("1003")); // the readout log carries the first missing control of the greedy walk
        Assert.Equal(FinishStatus.Mp, r["Missing"].Status);
        Assert.Equal(["32"], r["Missing"].Missed);
        Assert.Null(r["Missing"].ResultTime);
        Assert.Equal(FinishStatus.Dnf, r["NoFinish"].Status);
        Assert.Equal(FinishStatus.None, r["NotRead"].Status);
        Assert.False(r["NotRead"].HasReadout);
        Assert.True(r["Ok"].HasReadout);

        // Places: Extra 28:00 (1), Ok and Tie 30:00 (2, 2), nobody is 3rd.
        Assert.Equal(1, r["Extra"].Place);
        Assert.Equal(2, r["Ok"].Place);
        Assert.Equal(2, r["Tie"].Place);
        Assert.Null(r["WrongOrder"].Place);
        Assert.Null(r["NotRead"].Place);
    }

    [Fact]
    public async Task SetCourse_DisabledControlIsSkippedAndStartFinishIgnored()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("dis"));
        var points = await c.AddControlsAsync(1, "S1", "31", "32", "33", "F1");
        var broken = points.Single(p => p.Code == "32");
        broken.IsDisabled = true;
        await Sp.GetRequiredService<IEventStore>().UpdateControlPointAsync(c.Folder, broken);
        await c.AddGroupAsync("Ч21", "S1 31 32 33 F1");
        await c.AddRunnerAsync("A", "1", "1001", "Ч21", T(10, 0));
        await c.AddReadoutAsync(1, "1001", "31 33", T(10, 0), T(10, 30));

        var r = await c.ResultsAsync();
        Assert.Equal(FinishStatus.Ok, r["A"].Status);
        Assert.Equal(1, r["A"].Place);
    }

    [Fact]
    public async Task SetCourse_TimeLimitIsStrictAndNeedsAStart()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("ovt"));
        await c.AddControlsAsync(1, "31");
        await c.AddGroupAsync("Ч21", "31", s => s.TimeLimitSeconds = 30 * 60);
        await c.AddRunnerAsync("Exact", "1", "1001", "Ч21", T(10, 0));
        await c.AddRunnerAsync("Over", "2", "1002", "Ч21", T(10, 0));
        await c.AddRunnerAsync("NoStart", "3", "1003", null, null);
        await c.AddReadoutAsync(1, "1001", "31", T(10, 0), T(10, 30));
        await c.AddReadoutAsync(1, "1002", "31", T(10, 0), T(10, 30, 1));
        await c.AddReadoutAsync(1, "1003", "31", null, T(10, 45));

        var r = await c.ResultsAsync();
        Assert.Equal(FinishStatus.Ok, r["Exact"].Status);
        Assert.Equal(FinishStatus.Ovt, r["Over"].Status);
        Assert.Null(r["Over"].ResultTime);
        // Read out, but no start anywhere (no chip start, no start-protocol time): DNS, never a placed OK.
        Assert.Equal(FinishStatus.Dns, r["NoStart"].Status);
        Assert.Null(r["NoStart"].ResultTime);
        Assert.Null(r["NoStart"].Place);
    }

    [Fact]
    public async Task SetCourse_StartResolution_ChipStartWinsElseAssignedStartOnFinishDate()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("start"));
        await c.AddControlsAsync(1, "31");
        await c.AddGroupAsync("Ч21", "31");
        await c.AddRunnerAsync("Chip", "1", "1001", "Ч21", T(10, 0));
        await c.AddRunnerAsync("Assigned", "2", "1002", "Ч21", T(10, 0));
        await c.AddReadoutAsync(1, "1001", "31", T(10, 5), T(10, 35)); // chip says 10:05 although 10:00 was assigned
        await c.AddReadoutAsync(1, "1002", "31", null, T(10, 35));

        var r = await c.ResultsAsync();
        Assert.Equal(T(0, 30), r["Chip"].ResultTime);
        Assert.Equal(T(0, 35), r["Assigned"].ResultTime);
        Assert.Equal(c.Date + T(10, 5), r["Chip"].ActualStart);
        Assert.Null(r["Assigned"].ActualStart); // ActualStart is the chip's own start box only
    }

    [Fact]
    public async Task SetCourse_OverridesAndOutOfCompetition()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("ovr"));
        await c.AddControlsAsync(1, "31", "32");
        await c.AddGroupAsync("Ч21", "31 32");
        await c.AddRunnerAsync("Dsq", "1", "1001", "Ч21", T(10, 0), configureDay: d => d.ResultStatusOverride = FinishStatus.Dsq);
        await c.AddRunnerAsync("ForcedOk", "2", "1002", "Ч21", T(10, 0), configureDay: d => d.ResultStatusOverride = FinishStatus.Ok);
        await c.AddRunnerAsync("Dns", "3", "1003", "Ч21", T(10, 0), configureDay: d => d.ResultStatusOverride = FinishStatus.Dns);
        await c.AddRunnerAsync("Guest", "4", "1004", "Ч21", T(10, 0), configureDay: d => d.OutOfCompetition = true);
        await c.AddRunnerAsync("Regular", "5", "1005", "Ч21", T(10, 0));
        await c.AddReadoutAsync(1, "1001", "31 32", T(10, 0), T(10, 20));
        await c.AddReadoutAsync(1, "1002", "31", T(10, 0), T(10, 25));      // Mp, overridden to Ok → keeps a time
        await c.AddReadoutAsync(1, "1004", "31 32", T(10, 0), T(10, 10));   // fastest, but out of competition
        await c.AddReadoutAsync(1, "1005", "31 32", T(10, 0), T(10, 30));

        var r = await c.ResultsAsync();
        Assert.Equal(FinishStatus.Dsq, r["Dsq"].Status);
        Assert.Equal(FinishStatus.Ok, r["Dsq"].Computed);
        Assert.Equal(FinishStatus.Dsq, r["Dsq"].Override);
        Assert.Null(r["Dsq"].Place);
        Assert.Equal(FinishStatus.Ok, r["ForcedOk"].Status);
        Assert.Equal(FinishStatus.Mp, r["ForcedOk"].Computed);
        Assert.Equal(T(0, 25), r["ForcedOk"].ResultTime);
        Assert.Equal(FinishStatus.Dns, r["Dns"].Status);
        Assert.False(r["Dns"].HasReadout);
        Assert.True(r["Guest"].OutOfCompetition);
        Assert.Null(r["Guest"].Place);
        Assert.Equal(T(0, 10), r["Guest"].ResultTime);
        Assert.Equal(1, r["ForcedOk"].Place);
        Assert.Equal(2, r["Regular"].Place);
        Assert.Equal("П/К", ParticipantDayResult.OutOfCompetitionMark);
    }

    [Fact]
    public async Task SetCourse_LatestReadoutOfAChipWins()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("latest"));
        await c.AddControlsAsync(1, "31");
        await c.AddGroupAsync("Ч21", "31");
        await c.AddRunnerAsync("A", "1", "1001", "Ч21", T(10, 0));
        await c.AddReadoutAsync(1, "1001", "31", T(10, 0), T(10, 30));
        await c.AddReadoutAsync(1, "1001", "", T(10, 0), T(10, 31)); // re-read later: no punches → Mp

        var r = await c.ResultsAsync();
        Assert.Equal(FinishStatus.Mp, r["A"].Status);
    }

    [Fact]
    public async Task SetCourse_EmptyCourseOrNoGroupGivesOkWithTime()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("nogrp"));
        await c.AddGroupAsync("Відкрита", "");
        await c.AddRunnerAsync("InGroup", "1", "1001", "Відкрита", T(10, 0));
        await c.AddRunnerAsync("NoGroup", "2", "1002", null, T(10, 0));
        await c.AddReadoutAsync(1, "1001", "31 32", T(10, 0), T(10, 20));
        await c.AddReadoutAsync(1, "1002", "31", T(10, 0), T(10, 21));

        var r = await c.ResultsAsync();
        Assert.Equal(FinishStatus.Ok, r["InGroup"].Status);
        Assert.Equal(T(0, 20), r["InGroup"].ResultTime);
        Assert.Equal(FinishStatus.Ok, r["NoGroup"].Status);
    }

    [Fact]
    public async Task SetCourse_PerGroupDisciplineOverrideWins()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("gov"), discipline: DisciplineId.ScoreByCount);
        await c.AddControlsAsync(1, "31", "32", "33");
        await c.AddGroupAsync("Count", "31 32 33", s => s.RequiredControlCount = 2);
        await c.AddGroupAsync("Order", "31 32 33", s => s.DisciplineOverride = DisciplineId.SetCourse);
        await c.AddRunnerAsync("Counter", "1", "1001", "Count", T(10, 0));
        await c.AddRunnerAsync("Orderer", "2", "1002", "Order", T(10, 0));
        await c.AddReadoutAsync(1, "1001", "33 31", T(10, 0), T(10, 20));
        await c.AddReadoutAsync(1, "1002", "33 31", T(10, 0), T(10, 20));

        var r = await c.ResultsAsync();
        Assert.Equal(FinishStatus.Ok, r["Counter"].Status);
        Assert.Equal(FinishStatus.Mp, r["Orderer"].Status);
    }

    // ---- Score by count ------------------------------------------------------------------------------------

    [Fact]
    public async Task ScoreByCount_RequiredCountAndDetail()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("cnt"), discipline: DisciplineId.ScoreByCount);
        await c.AddControlsAsync(1, "31", "32", "33", "34");
        await c.AddGroupAsync("Ч21", "31 32 33 34", s => s.RequiredControlCount = 3);
        await c.AddGroupAsync("All", "31 32 33 34");
        await c.AddRunnerAsync("Enough", "1", "1001", "Ч21", T(10, 0));
        await c.AddRunnerAsync("Short", "2", "1002", "Ч21", T(10, 0));
        await c.AddRunnerAsync("Repeats", "3", "1003", "Ч21", T(10, 0));
        await c.AddRunnerAsync("NeedsAll", "4", "1004", "All", T(10, 0));
        await c.AddReadoutAsync(1, "1001", "34 31 32", T(10, 0), T(10, 20));
        await c.AddReadoutAsync(1, "1002", "31 99 32", T(10, 0), T(10, 20));
        await c.AddReadoutAsync(1, "1003", "31 31 31 32", T(10, 0), T(10, 20));
        await c.AddReadoutAsync(1, "1004", "31 32 33", T(10, 0), T(10, 20));

        var r = await c.ResultsAsync();
        Assert.Equal(FinishStatus.Ok, r["Enough"].Status);
        Assert.Equal(FinishStatus.Mp, r["Short"].Status);
        Assert.Equal("2/3", await DetailAsync("1002"));
        Assert.Equal(FinishDetailKind.ControlCount, (await RowAsync("1002")).StatusDetailKind);
        Assert.Equal(FinishStatus.Mp, r["Repeats"].Status);
        Assert.Equal(FinishStatus.Mp, r["NeedsAll"].Status);
        Assert.Equal("3/4", await DetailAsync("1004"));
        Assert.Null(r["Enough"].Score);
        Assert.Equal(1, r["Enough"].Place);
    }

    // ---- Score by time -------------------------------------------------------------------------------------

    [Fact]
    public async Task ScoreByTime_FinishIsOkLateCostsPointsNoFinishIsDnf()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("sbt"), discipline: DisciplineId.ScoreByTime);
        var cps = await c.AddControlsAsync(1, "31", "32", "33");
        var store = Sp.GetRequiredService<IEventStore>();
        foreach (var cp in cps) { cp.Points = int.Parse(cp.Code) / 10; await store.UpdateControlPointAsync(c.Folder, cp); } // 3 points each
        await c.AddGroupAsync("Ч21", "31 32 33", s => { s.TimeLimitSeconds = 20 * 60; s.PenaltyPerMinute = 2m; });
        await c.AddRunnerAsync("Clean", "1", "1001", "Ч21", T(10, 0));
        await c.AddRunnerAsync("Late", "2", "1002", "Ч21", T(10, 0));
        await c.AddRunnerAsync("Bonus", "3", "1003", "Ч21", T(10, 0), configureDay: d => d.Bonus = 4);
        await c.AddRunnerAsync("NoFinish", "4", "1004", "Ч21", T(10, 0));
        await c.AddRunnerAsync("Judged", "5", "1005", "Ч21", T(10, 0), configureDay: d => d.ResultStatusOverride = FinishStatus.Dsq);
        await c.AddReadoutAsync(1, "1001", "31 32 33", T(10, 0), T(10, 20));
        await c.AddReadoutAsync(1, "1002", "31 32 33", T(10, 0), T(10, 21, 30)); // 1:30 late → 2 started minutes × 2 = 4
        await c.AddReadoutAsync(1, "1003", "31 32", T(10, 0), T(10, 15));
        await c.AddReadoutAsync(1, "1004", "31 32 33", T(10, 0), T(10, 18));
        await c.AddReadoutAsync(1, "1005", "31 32 33", T(10, 0), T(10, 19));
        var noFinish = (await store.GetFinishReadoutsAsync(c.Folder, c.Day(1).Id)).Single(r => r.ChipNumber == "1004");
        noFinish.FinishTime = null;
        await store.UpdateFinishReadoutAsync(c.Folder, noFinish);

        var r = await c.ResultsAsync();
        Assert.Equal(FinishStatus.Ok, r["Clean"].Status);     // a finish is OK by itself, like the rogaine
        Assert.Equal(9, r["Clean"].Score);
        Assert.Equal(FinishStatus.Ok, r["Late"].Status);      // late only costs points, never OVT
        Assert.Equal(5, r["Late"].Score);                     // 9 − 2 started minutes × 2
        Assert.Equal(3, r["Late"].ScoreBreakdown.Count);
        Assert.Equal(10, r["Bonus"].Score);                   // 6 + bonus 4
        Assert.Equal(4, r["Bonus"].Bonus);
        Assert.Equal(FinishStatus.Dnf, r["NoFinish"].Status);
        Assert.Null(r["NoFinish"].Place);
        Assert.Equal(FinishStatus.Dsq, r["Judged"].Status);   // the judge's status still wins
        Assert.Equal(1, r["Bonus"].Place);
        Assert.Equal(2, r["Clean"].Place);
        Assert.Equal(3, r["Late"].Place);
    }

    // ---- Rogaine -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Rogaine_TeamScoreCommonControlsPenaltyAndPlaces()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("rog"), discipline: DisciplineId.Rogaine);
        var cps = await c.AddControlsAsync(1, "31", "42", "53", "64");
        var store = Sp.GetRequiredService<IEventStore>();
        foreach (var cp in cps) { cp.Points = int.Parse(cp.Code[..1]); await store.UpdateControlPointAsync(c.Folder, cp); } // 3,4,5,6
        await c.AddGroupAsync("Open", "31 42 53 64", s => s.TimeLimitSeconds = 60 * 60);

        // Team Alpha: both punched 31 42 53 (12 points); one also 64, which does not count; the slower one is 2:10 late → 3 minutes × 1 = −3.
        await c.AddRunnerAsync("A1", "1", "1001", "Open", T(10, 0), p => p.Team = "Alpha");
        await c.AddRunnerAsync("A2", "2", "1002", "Open", T(10, 0), p => p.Team = "Alpha");
        await c.AddReadoutAsync(1, "1001", "31 42 53 64", T(10, 0), T(10, 50));
        await c.AddReadoutAsync(1, "1002", "53 42 31", T(10, 0), T(11, 2, 10));
        // Team Beta: 31 42 (7 points), in time.
        await c.AddRunnerAsync("B1", "3", "1003", "Open", T(10, 0), p => p.Team = "Beta");
        await c.AddRunnerAsync("B2", "4", "1004", "Open", T(10, 0), p => p.Team = "Beta");
        await c.AddReadoutAsync(1, "1003", "31 42", T(10, 0), T(10, 40));
        await c.AddReadoutAsync(1, "1004", "42 31", T(10, 0), T(10, 41));
        // Team Gamma: one member never finished → unplaced.
        await c.AddRunnerAsync("G1", "5", "1005", "Open", T(10, 0), p => p.Team = "Gamma");
        await c.AddRunnerAsync("G2", "6", "1006", "Open", T(10, 0), p => p.Team = "Gamma");
        await c.AddReadoutAsync(1, "1005", "31 42 53 64", T(10, 0), T(10, 30));
        // Solo without a team: scored but never placed.
        await c.AddRunnerAsync("Solo", "7", "1007", "Open", T(10, 0));
        await c.AddReadoutAsync(1, "1007", "64", T(10, 0), T(10, 30));

        var r = await c.ResultsAsync();
        Assert.Equal(9, r["A1"].Score);
        Assert.Equal(9, r["A2"].Score);
        Assert.Equal(12, r["A1"].ScoreGross);
        Assert.Equal(3, r["A1"].ScorePenalty);
        Assert.Equal(FinishStatus.Ok, r["A2"].Status); // over time only costs points
        Assert.Equal(7, r["B1"].Score);
        Assert.Equal(1, r["A1"].Place);
        Assert.Equal(1, r["A2"].Place);
        Assert.Equal(2, r["B1"].Place);
        Assert.Equal(2, r["B2"].Place);
        Assert.Null(r["G1"].Place);
        Assert.Equal(FinishStatus.None, r["G2"].Status);
        Assert.Equal(6, r["Solo"].Score);
        Assert.Null(r["Solo"].Place);
    }

    [Fact]
    public async Task Rogaine_BonusIsTheMinimumEnteredAndPenaltyCannotGoNegative()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("rogb"), discipline: DisciplineId.Rogaine);
        var cps = await c.AddControlsAsync(1, "31");
        var store = Sp.GetRequiredService<IEventStore>();
        cps[0].Points = 3;
        await store.UpdateControlPointAsync(c.Folder, cps[0]);
        await c.AddGroupAsync("Open", "31", s => s.TimeLimitSeconds = 10 * 60);
        await c.AddRunnerAsync("X1", "1", "1001", "Open", T(10, 0), p => p.Team = "X", d => d.Bonus = 5);
        await c.AddRunnerAsync("X2", "2", "1002", "Open", T(10, 0), p => p.Team = "X", d => d.Bonus = 2);
        await c.AddReadoutAsync(1, "1001", "31", T(10, 0), T(10, 30)); // 20 minutes late: penalty capped at the gross 3
        await c.AddReadoutAsync(1, "1002", "31", T(10, 0), T(10, 5));

        var r = await c.ResultsAsync();
        Assert.Equal(2, r["X1"].Score); // 3 − 3 + min(5, 2)
        Assert.Equal(2, r["X2"].Bonus);
    }

    // ---- Mixed ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Mixed_JudgesAgainstThePattern()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("mix"), discipline: DisciplineId.Mixed);
        await c.AddControlsAsync(1, "41", "42", "45", "46", "47");
        await c.AddGroupAsync("Ч21", "<41 42> [2: 45 46 47]");
        await c.AddRunnerAsync("Ok", "1", "1001", "Ч21", T(10, 0));
        await c.AddRunnerAsync("Ok2", "2", "1002", "Ч21", T(10, 0));
        await c.AddRunnerAsync("Broken", "3", "1003", "Ч21", T(10, 0));
        await c.AddRunnerAsync("Short", "4", "1004", "Ч21", T(10, 0));
        await c.AddReadoutAsync(1, "1001", "41 42 46 45", T(10, 0), T(10, 20));
        await c.AddReadoutAsync(1, "1002", "41 42 47 46", T(10, 0), T(10, 21));
        await c.AddReadoutAsync(1, "1003", "42 41 45 46", T(10, 0), T(10, 20));
        await c.AddReadoutAsync(1, "1004", "41 42 45", T(10, 0), T(10, 20));

        var r = await c.ResultsAsync();
        Assert.Equal(FinishStatus.Ok, r["Ok"].Status);
        Assert.Equal(FinishStatus.Ok, r["Ok2"].Status);
        Assert.Equal(FinishStatus.Mp, r["Broken"].Status);
        Assert.Equal(FinishStatus.Mp, r["Short"].Status);
        Assert.Equal(1, r["Ok"].Place);
        Assert.Equal(2, r["Ok2"].Place);

        var provider = Sp.GetRequiredService<IDisciplineStrategyProvider>();
        Assert.Equal(4, provider.For(DisciplineId.Mixed).ControlCount("<41 42> [2: 45 46 47]"));
    }

    // ---- Scatter -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Scatter_DetectsTheVariantPerRunner()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("scat"), discipline: DisciplineId.Scatter);
        await c.AddControlsAsync(1, "31", "32", "33");
        var group = await c.AddGroupAsync("Ч21", "31 32 33");
        var store = Sp.GetRequiredService<IEventStore>();
        await store.ReplaceCourseVariantsForGroupAsync(c.Folder, c.Day(1).Id, group.Id,
        [
            new GroupCourseVariant { EventDayId = c.Day(1).Id, GroupId = group.Id, Code = "A", CourseOrder = "31 32 33", Order = 0 },
            new GroupCourseVariant { EventDayId = c.Day(1).Id, GroupId = group.Id, Code = "B", CourseOrder = "33 32 31", Order = 1 },
        ]);
        await c.AddRunnerAsync("A", "1", "1001", "Ч21", T(10, 0));
        await c.AddRunnerAsync("B", "2", "1002", "Ч21", T(10, 0));
        await c.AddRunnerAsync("Neither", "3", "1003", "Ч21", T(10, 0));
        await c.AddReadoutAsync(1, "1001", "31 32 33", T(10, 0), T(10, 20));
        await c.AddReadoutAsync(1, "1002", "33 32 31", T(10, 0), T(10, 19));
        await c.AddReadoutAsync(1, "1003", "32 31 33", T(10, 0), T(10, 18));

        var r = await c.ResultsAsync();
        Assert.Equal(FinishStatus.Ok, r["A"].Status);
        Assert.Equal(FinishStatus.Ok, r["B"].Status);
        Assert.Equal(FinishStatus.Mp, r["Neither"].Status);
        Assert.Equal(1, r["B"].Place);

        var editor = Sp.GetRequiredService<ICompetitionEditorService>();
        var rows = await editor.GetFinishReadoutRowsAsync();
        var splitsB = await editor.GetFinishSplitsAsync(rows.Single(x => x.ChipNumber == "1002").Id);
        Assert.NotNull(splitsB);
        Assert.Equal("B", splitsB.VariantCode);
        var variants = await editor.GetCourseVariantsAsync(group.Id);
        Assert.Equal(["A", "B"], variants.Select(v => v.Code));
    }

    // ---- Points and ranks ----------------------------------------------------------------------------------

    [Fact]
    public async Task Points_TableRuleAndFormulaRulePerGroup()
    {
        var app = Sp.GetRequiredService<IAppStore>();
        var table = await app.AddPointsRuleAsync(PointsRuleKind.Table);
        table.Name = "test table"; table.TableJson = "[30,20,10]";
        await app.UpdatePointsRuleAsync(table);
        var formula = await app.AddPointsRuleAsync(PointsRuleKind.Formula);
        formula.Name = "test formula"; formula.Formula = "100*(2 - T_у/T_л)";
        await app.UpdatePointsRuleAsync(formula);

        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("pts"));
        await c.AddControlsAsync(1, "31");
        await c.AddGroupAsync("Tab", "31", s => s.PointsRuleId = table.Id);
        await c.AddGroupAsync("Form", "31", s => s.PointsRuleId = formula.Id);
        await c.AddGroupAsync("None", "31", s => s.PointsRuleId = Guid.Empty);
        foreach (var (g, i) in new[] { ("Tab", 1), ("Form", 2), ("None", 3) })
        {
            await c.AddRunnerAsync($"{g}1", $"{i}1", $"10{i}1", g, T(10, 0));
            await c.AddRunnerAsync($"{g}2", $"{i}2", $"10{i}2", g, T(10, 0));
            await c.AddRunnerAsync($"{g}Mp", $"{i}3", $"10{i}3", g, T(10, 0));
            await c.AddReadoutAsync(1, $"10{i}1", "31", T(10, 0), T(10, 10));
            await c.AddReadoutAsync(1, $"10{i}2", "31", T(10, 0), T(10, 12));
            await c.AddReadoutAsync(1, $"10{i}3", "", T(10, 0), T(10, 9));
        }

        var r = await c.ResultsAsync();
        Assert.Equal(30m, r["Tab1"].Points);
        Assert.Equal(20m, r["Tab2"].Points);
        Assert.Null(r["TabMp"].Points);
        Assert.Equal(100m, r["Form1"].Points);
        Assert.Equal(80m, r["Form2"].Points);   // 100·(2 − 720/600)
        Assert.Null(r["None1"].Points);
    }

    [Fact]
    public async Task Points_DefaultRuleOfTheCompetitionAppliesWhenTheGroupHasNone()
    {
        var app = Sp.GetRequiredService<IAppStore>();
        var table = await app.AddPointsRuleAsync(PointsRuleKind.Table);
        table.Name = "default table"; table.TableJson = "[5,3]";
        await app.UpdatePointsRuleAsync(table);

        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("ptsd"));
        var store = Sp.GetRequiredService<IEventStore>();
        var info = await store.GetCompetitionInfoAsync(c.Folder);
        info!.DefaultPointsRuleId = table.Id;
        await store.SaveCompetitionInfoAsync(c.Folder, info);
        await c.AddControlsAsync(1, "31");
        await c.AddGroupAsync("G", "31");
        await c.AddRunnerAsync("First", "1", "1001", "G", T(10, 0));
        await c.AddReadoutAsync(1, "1001", "31", T(10, 0), T(10, 10));

        var r = await c.ResultsAsync();
        Assert.Equal(5m, r["First"].Points);
    }

    // Another instance (another install, the dev build, another computer) has its own app.db, where the rule's id
    // does not exist — simulated by deleting the rule after the competition was opened once.
    [Fact]
    public async Task Points_RuleUnknownToThisAppDbScoresFromTheCompetitionCopy()
    {
        var app = Sp.GetRequiredService<IAppStore>();
        var table = await app.AddPointsRuleAsync(PointsRuleKind.Table);
        table.Name = "copied table"; table.TableJson = "[7,4]";
        await app.UpdatePointsRuleAsync(table);

        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("ptsc"));
        await c.AddControlsAsync(1, "31");
        await c.AddGroupAsync("G", "31", s => s.PointsRuleId = table.Id);
        await c.AddRunnerAsync("First", "1", "1001", "G", T(10, 0));
        await c.AddRunnerAsync("Second", "2", "1002", "G", T(10, 0));
        await c.AddReadoutAsync(1, "1001", "31", T(10, 0), T(10, 10));
        await c.AddReadoutAsync(1, "1002", "31", T(10, 0), T(10, 12));

        // Opening the competition writes the copy.
        Assert.Equal(7m, (await c.ResultsAsync())["First"].Points);
        var store = Sp.GetRequiredService<IEventStore>();
        var copy = Assert.Single(PointsRuleCopies.Parse((await store.GetCompetitionInfoAsync(c.Folder))!.PointsRulesJson));
        Assert.Equal(table.Id, copy.Id);
        Assert.Equal("[7,4]", copy.TableJson);

        await app.DeletePointsRuleAsync(table.Id);

        var r = await c.ResultsAsync();
        Assert.Equal(7m, r["First"].Points);
        Assert.Equal(4m, r["Second"].Points);
        // Reopening keeps a copy this catalogue cannot refresh, and the Groups page offers it as the competition's.
        Assert.Single(PointsRuleCopies.Parse((await store.GetCompetitionInfoAsync(c.Folder))!.PointsRulesJson));
        var choices = await Sp.GetRequiredService<ICompetitionEditorService>().GetPointsRuleChoicesAsync();
        Assert.True(Assert.Single(choices, ch => ch.Rule.Id == table.Id).IsCompetitionCopy);
    }

    [Fact]
    public async Task Points_CompetitionCopyFollowsTheDefaultRuleAndItsEdits()
    {
        var app = Sp.GetRequiredService<IAppStore>();
        var table = await app.AddPointsRuleAsync(PointsRuleKind.Table);
        table.Name = "copied default"; table.TableJson = "[9]";
        await app.UpdatePointsRuleAsync(table);

        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("ptse"));
        await c.SelectAsync();
        var editor = Sp.GetRequiredService<ICompetitionEditorService>();
        var store = Sp.GetRequiredService<IEventStore>();
        async Task<IReadOnlyList<PointsRule>> CopiesAsync()
            => PointsRuleCopies.Parse((await store.GetCompetitionInfoAsync(c.Folder))!.PointsRulesJson);

        var info = (await editor.GetInfoAsync())!;
        info.DefaultPointsRuleId = table.Id;
        await editor.SaveInfoAsync(info);
        Assert.Equal("[9]", Assert.Single(await CopiesAsync()).TableJson);

        // A page saving an older CompetitionInfo leaves the copy alone.
        info.Name = "renamed";
        info.PointsRulesJson = null;
        await store.SaveCompetitionInfoAsync(c.Folder, info);
        Assert.Single(await CopiesAsync());

        table.TableJson = "[11]";
        await app.UpdatePointsRuleAsync(table);
        await editor.RefreshPointsRuleCopiesAsync();
        Assert.Equal("[11]", Assert.Single(await CopiesAsync()).TableJson);

        info = (await editor.GetInfoAsync())!;
        info.DefaultPointsRuleId = null;
        await editor.SaveInfoAsync(info);
        await editor.RefreshPointsRuleCopiesAsync();
        Assert.Empty(await CopiesAsync());
    }

    [Fact]
    public async Task Ranks_AwardedByPercentOfLeaderWhenConditionsHold()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("rank"));
        var store = Sp.GetRequiredService<IEventStore>();
        // Rank conditions need ≥ minRegions distinct regions among the day's runners; create them.
        var app = Sp.GetRequiredService<IAppStore>();
        var conditions = await app.GetRankConditionsAsync() ?? (3, 8, 12);
        var regions = new List<Region>();
        for (var i = 0; i < Math.Max(conditions.MinRegions, 1); i++)
        {
            var region = new Region { Name = $"Регіон {i}" };
            await store.AddRegionAsync(c.Folder, region);
            regions.Add(region);
        }
        await c.AddControlsAsync(1, "31");
        await c.AddGroupAsync("Ч21", "31", s => { s.RankLevel = GroupRankLevel.Adult; s.MasterCount = 1; });
        var rankNames = (await app.GetRanksAsync()).Select(x => x.Name).ToList();
        for (var i = 0; i < Math.Max(conditions.MinParticipants, regions.Count); i++)
        {
            var region = regions[i % regions.Count];
            await c.AddRunnerAsync($"R{i}", $"{i + 1}", $"20{i:00}", "Ч21", T(10, 0), p => { p.RegionId = region.Id; p.Rank = rankNames.Contains("МСУ") ? "МСУ" : rankNames[0]; });
            await c.AddReadoutAsync(1, $"20{i:00}", "31", T(10, 0), T(10, 10 + i * 2));
        }

        var r = await c.ResultsAsync();
        Assert.Equal("МСУ", r["R0"].AwardedRank);            // place 1 ≤ MasterCount
        Assert.True(r.Values.Count(x => x.AwardedRank is not null) >= 1);
        var second = r["R1"];
        Assert.Equal(2, second.Place);
        // 12:00 vs 10:00 = 120 % of the leader → whatever the seeded table says for this course rank, never МСУ.
        Assert.NotEqual("МСУ", second.AwardedRank);
    }

    [Fact]
    public async Task Ranks_NotAwardedWithTooFewRegions()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("rankno"));
        await c.AddControlsAsync(1, "31");
        await c.AddGroupAsync("Ч21", "31", s => { s.RankLevel = GroupRankLevel.Adult; s.MasterCount = 1; });
        for (var i = 0; i < 4; i++)
        {
            await c.AddRunnerAsync($"R{i}", $"{i + 1}", $"21{i:00}", "Ч21", T(10, 0), p => p.Rank = "МСУ");
            await c.AddReadoutAsync(1, $"21{i:00}", "31", T(10, 0), T(10, 10 + i));
        }
        var r = await c.ResultsAsync();
        Assert.All(r.Values, x => Assert.Null(x.AwardedRank));
    }
}
