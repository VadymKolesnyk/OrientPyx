using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Tests.Support;

namespace OrientPyx.Tests.Core;

/// <summary>
/// The relay (естафета): leg starts from the previous finish, mass start, restarts and their ranking modes,
/// team status and time, incomplete teams, course assignment modes, and how the teams reach the protocol and
/// the online snapshot. Built on the worked examples of docs/relay-scenarios.md.
/// </summary>
[Collection(AppCollection.Name)]
public sealed class RelayTests
{
    private static IServiceProvider Sp => TestEnvironment.Instance.Services;

    private static TimeSpan T(int h, int m, int s = 0) => new(h, m, s);

    /// <summary>Three-leg relay with mass start 10:00: a team per scenario.</summary>
    private static async Task<CompetitionBuilder> BuildBasicAsync()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("relay"), discipline: DisciplineId.Relay);
        await c.AddControlsAsync(1, "31", "32", "33");
        await c.AddGroupAsync("R", "31 32 33", s => { s.LegCount = 3; s.MassStartTime = T(10, 0); });

        async Task Team(int number, params (string Name, string Chip, int Leg, string? Punches, TimeSpan? Finish)[] legs)
        {
            foreach (var (name, chip, _, _, _) in legs)
                await c.AddRunnerAsync(name, "", chip, "R");
            await c.AddRelayTeamAsync("R", number, $"Team {number}", 1, legs.Select(l => (l.Name, l.Leg)).ToArray());
            foreach (var (_, chip, _, punches, finish) in legs)
                if (punches is not null && finish is { } f)
                    await c.AddReadoutAsync(1, chip, punches, null, f);
        }

        await Team(101, ("101-1", "1011", 1, "31 32 33", T(10, 20)), ("101-2", "1012", 2, "31 32 33", T(10, 45)), ("101-3", "1013", 3, "31 32 33", T(11, 5)));
        await Team(106, ("106-1", "1061", 1, "31 32 33", T(10, 20)), ("106-2", "1062", 2, "31 32 33", T(10, 45)), ("106-3", "1063", 3, "31 32 33", T(11, 5)));
        await Team(102, ("102-1", "1021", 1, "31 32 33", T(10, 20)), ("102-2", "1022", 2, "31 33", T(10, 40)), ("102-3", "1023", 3, "31 32 33", T(11, 0)));
        await Team(103, ("103-1", "1031", 1, "31 32 33", T(10, 20)), ("103-2", "1032", 2, null, null), ("103-3", "1033", 3, "31 32 33", T(11, 0)));
        await Team(105, ("105-1", "1051", 1, "31 32 33", T(10, 21)), ("105-2", "1052", 2, "31 32 33", T(10, 44)));
        await Team(107, ("107-1", "1071", 1, "31 32 33", T(10, 30)), ("107-2", "1072", 2, "31 32 33", T(10, 50)), ("107-3", "1073", 3, "31 32 33", T(11, 20)));
        return c;
    }

    [Fact]
    public async Task LegsChainFromTheMassStartAndTeamsRank()
    {
        var c = await BuildBasicAsync();
        var r = await c.ResultsAsync();

        var t101 = r["101-1"].Team!;
        Assert.Equal(FinishStatus.Ok, t101.Status);
        Assert.Equal(T(1, 5), t101.Time);
        Assert.Equal(1, t101.Place);
        Assert.Equal([T(0, 20), T(0, 25), T(0, 20)], t101.Legs.Select(l => l.LegTime));
        Assert.Equal([T(0, 20), T(0, 45), T(1, 5)], t101.Legs.Select(l => l.Cumulative));
        Assert.Equal(T(0, 25), r["101-2"].ResultTime);
        Assert.Equal("101.2", r["101-2"].LegBib);
        Assert.Equal(2, r["101-2"].Leg!.Leg);
        Assert.Equal(1, r["101-3"].Place);                 // the team's place reaches every member
        Assert.Equal(1, r["106-1"].Team!.Place);            // same time → shared first place
        Assert.Equal(3, r["107-1"].Team!.Place);            // next place skips the tie
        Assert.False(t101.Restarted);
    }

    [Fact]
    public async Task AMissedControlOnALegMakesTheTeamMpAndTheNextLegStillStarts()
    {
        var c = await BuildBasicAsync();
        var r = await c.ResultsAsync();

        Assert.Equal(FinishStatus.Mp, r["102-2"].Status);
        Assert.Equal("32", r["102-2"].StatusDetail);
        var team = r["102-1"].Team!;
        Assert.Equal(FinishStatus.Mp, team.Status);
        Assert.Null(team.Time);
        Assert.Null(team.Place);
        Assert.Equal(FinishStatus.Ok, r["102-3"].Status);
        Assert.Equal(T(0, 20), r["102-3"].ResultTime);     // leg 3 started at leg 2's finish, 10:40
    }

    [Fact]
    public async Task AReadLegWithoutAPreviousFinishIsDnsUntilThePreviousLegIsRead()
    {
        // Р-30: a leg starts at the previous leg's finish; with none (the previous leg not read, or read without a
        // finish punch) and no restart, the leg has no start — DNS, whatever its punches.
        var c = await BuildBasicAsync();
        var r = await c.ResultsAsync();

        Assert.Equal(FinishStatus.None, r["103-2"].Status);         // not read: still on course
        Assert.Equal(FinishStatus.Dns, r["103-3"].Status);
        Assert.Null(r["103-3"].Leg!.Start);
        Assert.Null(r["103-3"].ResultTime);
        Assert.Equal(FinishStatus.Dns, r["103-1"].Team!.Status);

        // Read without a finish punch: still no start for leg 3.
        var noFinish = await c.AddReadoutAsync(1, "1032", "31 32 33", null, T(10, 40));
        var store = Sp.GetRequiredService<IEventStore>();
        noFinish.FinishTime = null;
        await store.UpdateFinishReadoutAsync(c.Folder, noFinish);
        r = await c.ResultsAsync();
        Assert.Equal(FinishStatus.Dnf, r["103-2"].Status);
        Assert.Equal(FinishStatus.Dns, r["103-3"].Status);

        // A finish from the previous leg gives leg 3 its start: judged as usual.
        await c.AddReadoutAsync(1, "1032", "31 32 33", null, T(10, 40));
        r = await c.ResultsAsync();
        Assert.Equal(FinishStatus.Ok, r["103-3"].Status);
        Assert.Equal(T(0, 20), r["103-3"].ResultTime);
        Assert.Equal(FinishStatus.Ok, r["103-1"].Team!.Status);
    }

    [Fact]
    public async Task ALegOneWithNoStartAnywhereIsDns()
    {
        // Р-27: leg 1 runs from its start punch, else the runner's start time, else the mass start; none — DNS.
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("rdns1"), discipline: DisciplineId.Relay);
        await c.AddControlsAsync(1, "31");
        await c.AddGroupAsync("R", "31", s => s.LegCount = 1);
        await c.AddRunnerAsync("NoStart", "", "7101", "R");
        await c.AddRunnerAsync("OwnStart", "", "7201", "R", T(10, 0));
        await c.AddRunnerAsync("Punched", "", "7301", "R");
        await c.AddRelayTeamAsync("R", 71, "71", 1, ("NoStart", 1));
        await c.AddRelayTeamAsync("R", 72, "72", 1, ("OwnStart", 1));
        await c.AddRelayTeamAsync("R", 73, "73", 1, ("Punched", 1));
        await c.AddReadoutAsync(1, "7101", "31", null, T(10, 20));
        await c.AddReadoutAsync(1, "7201", "31", null, T(10, 20));
        await c.AddReadoutAsync(1, "7301", "31", T(10, 5), T(10, 20));

        var r = await c.ResultsAsync();
        Assert.Equal(FinishStatus.Dns, r["NoStart"].Status);
        Assert.Equal((FinishStatus.Ok, T(0, 20)), (r["OwnStart"].Status, r["OwnStart"].ResultTime!.Value));
        Assert.Equal((FinishStatus.Ok, T(0, 15)), (r["Punched"].Status, r["Punched"].ResultTime!.Value));
    }

    [Fact]
    public async Task AnEarlierReadOutOfALegChipIsJudgedFromTheLegsStartNotDns()
    {
        // The read-out log: leg 2's chip read twice — the first read missed 32. It is judged like the leg — from leg 1's
        // finish — so MP, not DNS (a lone leg 2 has no start of its own); the latest read is the leg's result.
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("rearly"), discipline: DisciplineId.Relay);
        await c.AddControlsAsync(1, "31", "32", "33");
        await c.AddGroupAsync("R", "31 32 33", s => { s.LegCount = 2; s.MassStartTime = T(10, 0); });
        await c.AddRunnerAsync("E-1", "", "8101", "R");
        await c.AddRunnerAsync("E-2", "", "8102", "R");
        await c.AddRelayTeamAsync("R", 81, "81", 1, ("E-1", 1), ("E-2", 2));
        var leg1Early = await c.AddReadoutAsync(1, "8101", "31 32", null, T(10, 18));
        await c.AddReadoutAsync(1, "8101", "31 32 33", null, T(10, 20));
        var leg2Early = await c.AddReadoutAsync(1, "8102", "31 33", null, T(10, 35));
        var leg2Late = await c.AddReadoutAsync(1, "8102", "31 32 33", null, T(10, 40));

        var r = await c.ResultsAsync();
        Assert.Equal(FinishStatus.Ok, r["E-2"].Status);

        var rows = (await Sp.GetRequiredService<ICompetitionEditorService>().GetFinishReadoutRowsAsync()).ToDictionary(x => x.Id);
        Assert.Equal((FinishStatus.Mp, "32", T(0, 15)), (rows[leg2Early.Id].Status, rows[leg2Early.Id].StatusDetail, rows[leg2Early.Id].Elapsed!.Value));
        Assert.Equal((FinishStatus.Ok, T(0, 20)), (rows[leg2Late.Id].Status, rows[leg2Late.Id].Elapsed!.Value));
        // Leg 1's earlier read runs from the mass start.
        Assert.Equal((FinishStatus.Mp, T(0, 18)), (rows[leg1Early.Id].Status, rows[leg1Early.Id].Elapsed!.Value));
    }

    [Fact]
    public async Task ATeamWithAVacantLegIsIncompleteAndOutOfCompetition()
    {
        var c = await BuildBasicAsync();
        var r = await c.ResultsAsync();

        var team = r["105-1"].Team!;
        Assert.Equal(FinishStatus.Ok, team.Status);
        Assert.Equal(T(0, 44), team.Time);
        Assert.True(team.Incomplete);
        Assert.True(team.OutOfCompetition);
        Assert.Null(team.Place);
        Assert.True(r["105-2"].OutOfCompetition);
    }

    [Fact]
    public async Task RestartsAreRankedPerMode()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("restart"), discipline: DisciplineId.Relay);
        await c.AddControlsAsync(1, "31");
        var modes = new[] { RelayRestartRanking.AfterClean, RelayRestartRanking.Normal, RelayRestartRanking.Unranked };
        foreach (var mode in modes)
        {
            var g = $"R-{mode}";
            await c.AddGroupAsync(g, "31", s => { s.LegCount = 3; s.MassStartTime = T(10, 0); s.RestartTimesJson = "{\"3\":\"11:00:00\"}"; s.RestartRanking = mode; });
            // 201: leg 2 finishes 11:10, after the 11:00 restart → leg 3 restarted at 11:00, total 20 + 50 + 15 = 1:25.
            // 202: 25 + 30 + 35 = 1:30, no restart (leg 3 started 10:55 < 11:00).
            foreach (var (team, legs) in new[]
                     {
                         (201, new[] { ("31", T(10, 20)), ("31", T(11, 10)), ("31", T(11, 15)) }),
                         (202, new[] { ("31", T(10, 25)), ("31", T(10, 55)), ("31", T(11, 30)) }),
                     })
            {
                var names = new List<(string, int)>();
                for (var leg = 1; leg <= 3; leg++)
                {
                    var name = $"{g}-{team}-{leg}";
                    var chip = $"{(int)mode + 5}{team}{leg}";
                    await c.AddRunnerAsync(name, "", chip, g);
                    names.Add((name, leg));
                    await c.AddReadoutAsync(1, chip, legs[leg - 1].Item1, null, legs[leg - 1].Item2);
                }
                await c.AddRelayTeamAsync(g, team + (int)mode * 10, $"{team}", 1, names.ToArray());
            }
        }

        var r = await c.ResultsAsync();
        var afterClean201 = r["R-AfterClean-201-1"].Team!;
        var afterClean202 = r["R-AfterClean-202-1"].Team!;
        Assert.True(afterClean201.Restarted);
        Assert.True(afterClean201.Legs[2].Restarted);
        Assert.Equal(T(1, 25), afterClean201.Time);
        Assert.Equal(T(1, 30), afterClean202.Time);
        Assert.Equal(1, afterClean202.Place);               // clean teams first
        Assert.Equal(2, afterClean201.Place);

        Assert.Equal(1, r["R-Normal-201-1"].Team!.Place);   // faster wins regardless of the restart
        Assert.Equal(2, r["R-Normal-202-1"].Team!.Place);

        Assert.True(r["R-Unranked-201-1"].Team!.OutOfCompetition);
        Assert.Null(r["R-Unranked-201-1"].Team!.Place);
        Assert.Equal(1, r["R-Unranked-202-1"].Team!.Place);
    }

    [Fact]
    public async Task TeamTimeLimitAndStatusOverride()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("rlim"), discipline: DisciplineId.Relay);
        await c.AddControlsAsync(1, "31");
        await c.AddGroupAsync("R", "31", s => { s.LegCount = 2; s.MassStartTime = T(10, 0); s.TimeLimitSeconds = 30 * 60; });
        await c.AddRunnerAsync("Slow-1", "", "3011", "R");
        await c.AddRunnerAsync("Slow-2", "", "3012", "R");
        await c.AddRunnerAsync("Dsq-1", "", "3021", "R");
        await c.AddRunnerAsync("Dsq-2", "", "3022", "R");
        await c.AddRelayTeamAsync("R", 301, "Slow", 1, ("Slow-1", 1), ("Slow-2", 2));
        var dsq = await c.AddRelayTeamAsync("R", 302, "Dsq", 1, ("Dsq-1", 1), ("Dsq-2", 2));
        dsq.StatusOverride = FinishStatus.Dsq;
        await Sp.GetRequiredService<IEventStore>().UpdateRelayTeamAsync(c.Folder, dsq);
        await c.AddReadoutAsync(1, "3011", "31", null, T(10, 20));
        await c.AddReadoutAsync(1, "3012", "31", null, T(10, 30, 1));
        await c.AddReadoutAsync(1, "3021", "31", null, T(10, 10));
        await c.AddReadoutAsync(1, "3022", "31", null, T(10, 20));

        var r = await c.ResultsAsync();
        Assert.Equal(FinishStatus.Ovt, r["Slow-1"].Team!.Status);
        Assert.Equal(T(0, 30, 1), r["Slow-1"].Team!.ActualTime);
        Assert.Equal(FinishStatus.Dsq, r["Dsq-1"].Team!.Status);
        Assert.Equal(FinishStatus.Ok, r["Dsq-1"].Team!.Computed);
        Assert.Null(r["Dsq-1"].Team!.Place);
    }

    [Fact]
    public async Task LegCoursesByBibAndTeamSet()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("rcourse"), discipline: DisciplineId.Relay);
        await c.AddControlsAsync(1, "31", "32");
        foreach (var g in new[] { "ByBib", "TeamSet", "OcadSet" })
        {
            await c.AddGroupAsync(g, "31 32", s => { s.LegCount = 2; s.MassStartTime = T(10, 0); });
            await c.SetCourseVariantsAsync(g, 1, ("A", "31 32"), ("B", "32 31"));
        }
        await c.SetRelayCourseModeAsync("ByBib", 1, RelayCourseMode.ByBib);
        await c.SetRelayCourseModeAsync("TeamSet", 1, RelayCourseMode.TeamSet, teamCourseSet: "A B");
        // No set of its own: each team's set is the courses assigned to its leg numbers (OCAD's team combination).
        await c.SetRelayCourseModeAsync("OcadSet", 1, RelayCourseMode.TeamSet);

        async Task Team(string g, int number, string leg1, string leg2)
        {
            await c.AddRunnerAsync($"{g}-{number}-1", "", $"{number}1", g);
            await c.AddRunnerAsync($"{g}-{number}-2", "", $"{number}2", g);
            await c.AddRelayTeamAsync(g, number, $"{number}", 1, ($"{g}-{number}-1", 1), ($"{g}-{number}-2", 2));
            await c.AddReadoutAsync(1, $"{number}1", leg1, null, T(10, 20));
            await c.AddReadoutAsync(1, $"{number}2", leg2, null, T(10, 40));
        }

        await Team("ByBib", 411, "32 31", "31 32");   // assigned B then A
        await Team("ByBib", 412, "31 32", "31 32");   // leg 2 has no assignment → course not assigned
        await Team("TeamSet", 421, "32 31", "31 32"); // set {A, B}: legs matched to B and A → OK
        await Team("TeamSet", 422, "31 32", "31 32"); // both ran A: one leg must take B → MP
        await Team("OcadSet", 431, "31 32", "32 31"); // assigned B, A; the legs swapped them → still the set → OK
        await Team("OcadSet", 432, "31 32", "31 32"); // assigned A, B; both ran A → MP

        var store = Sp.GetRequiredService<IEventStore>();
        await store.UpsertLegCourseAssignmentsAsync(c.Folder, c.Day(1).Id,
        [
            new LegCourseAssignment { EventDayId = c.Day(1).Id, TeamNumber = 411, Leg = 1, CourseKey = "B" },
            new LegCourseAssignment { EventDayId = c.Day(1).Id, TeamNumber = 411, Leg = 2, CourseKey = "A" },
            new LegCourseAssignment { EventDayId = c.Day(1).Id, TeamNumber = 412, Leg = 1, CourseKey = "A" },
            // Another group's row on the same leg number never applies to this team.
            new LegCourseAssignment { EventDayId = c.Day(1).Id, TeamNumber = 412, Leg = 2, CourseKey = "A", GroupId = c.Groups["TeamSet"].Id },
            new LegCourseAssignment { EventDayId = c.Day(1).Id, TeamNumber = 431, Leg = 1, CourseKey = "B" },
            new LegCourseAssignment { EventDayId = c.Day(1).Id, TeamNumber = 431, Leg = 2, CourseKey = "A" },
            new LegCourseAssignment { EventDayId = c.Day(1).Id, TeamNumber = 432, Leg = 1, CourseKey = "A" },
            new LegCourseAssignment { EventDayId = c.Day(1).Id, TeamNumber = 432, Leg = 2, CourseKey = "B" },
        ]);

        var r = await c.ResultsAsync();
        Assert.Equal(FinishStatus.Ok, r["ByBib-411-1"].Team!.Status);
        Assert.True(r["ByBib-412-2"].Leg!.CourseUnassigned);
        Assert.Equal(FinishStatus.Mp, r["ByBib-412-2"].Status);
        Assert.Equal(FinishDetailKind.CourseNotAssigned, r["ByBib-412-2"].StatusDetailKind);
        Assert.Equal(FinishStatus.Ok, r["TeamSet-421-1"].Team!.Status);
        Assert.Equal("B", r["TeamSet-421-1"].Leg!.CourseCode);
        Assert.Equal("A", r["TeamSet-421-2"].Leg!.CourseCode);
        Assert.Equal(FinishStatus.Mp, r["TeamSet-422-1"].Team!.Status);
        Assert.Equal(FinishStatus.Ok, r["OcadSet-431-1"].Team!.Status);
        Assert.Equal("A", r["OcadSet-431-1"].Leg!.CourseCode);
        Assert.Equal("B", r["OcadSet-431-2"].Leg!.CourseCode);
        Assert.Equal(FinishStatus.Mp, r["OcadSet-432-1"].Team!.Status);
    }

    [Fact]
    public async Task EveryLegItsOwnCourse()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("rperleg"), discipline: DisciplineId.Relay);
        await c.AddControlsAsync(1, "31", "32", "33", "34", "35");
        await c.AddGroupAsync("R", "31 32 33", s => { s.LegCount = 3; s.MassStartTime = T(10, 0); });
        // Leg 1: a plain order coded with the leg number (as typed before the «Етап» column). Leg 2: two courses — a
        // mixed pattern (31 and 32 in any order, then 33) and a plain one; the runner gets the one they ran. Leg 3: none.
        // A course with no leg and one on a leg the relay lacks are never run.
        await c.SetLegCoursesAsync("R", 1,
            ("1", 1, "31 32"), ("B", 2, "[2: 31 32] 33"), ("C", 2, "34 35"), ("X", null, "33"), ("Z", 4, "33"));
        await c.SetRelayCourseModeAsync("R", 1, RelayCourseMode.PerLeg);

        async Task Team(int number, string leg1, string? leg2, string leg3)
        {
            await c.AddRunnerAsync($"{number}-1", "", $"{number}1", "R");
            await c.AddRunnerAsync($"{number}-2", "", $"{number}2", "R");
            await c.AddRunnerAsync($"{number}-3", "", $"{number}3", "R");
            await c.AddRelayTeamAsync("R", number, $"{number}", 1, ($"{number}-1", 1), ($"{number}-2", 2), ($"{number}-3", 3));
            await c.AddReadoutAsync(1, $"{number}1", leg1, null, T(10, 20));
            if (leg2 is not null)
                await c.AddReadoutAsync(1, $"{number}2", leg2, null, T(10, 40));
            if (leg3.Length > 0)
                await c.AddReadoutAsync(1, $"{number}3", leg3, null, T(11, 0));
        }

        await Team(501, "31 32", "32 31 33", "");   // leg 2 swapped B's any-order block → OK on B; leg 3 not read
        await Team(502, "32 31", "34 35", "");      // leg 1 is a set order → MP; leg 2 ran C → OK on C
        await Team(503, "31 32", "31 33 32", "33"); // leg 2 ran 33 before B's block was done → MP on B; leg 3 has no course
        await Team(504, "31 32", null, "");         // leg 2 not read: two courses, none determined yet

        var r = await c.ResultsAsync();
        Assert.Equal(FinishStatus.Ok, r["501-1"].Status);
        Assert.Equal("1", r["501-1"].Leg!.CourseCode);
        Assert.True(r["501-1"].Leg!.CoursePerLeg);
        Assert.Null(r["501-1"].Leg!.ShownCourseCode);           // «1» on leg 1: the leg number already names it
        Assert.Equal(FinishStatus.Ok, r["501-2"].Status);
        Assert.Equal("B", r["501-2"].Leg!.ShownCourseCode);
        Assert.Equal(FinishStatus.Mp, r["502-1"].Status);
        Assert.Equal(FinishStatus.Ok, r["502-2"].Status);
        Assert.Equal("C", r["502-2"].Leg!.CourseCode);
        Assert.Equal(FinishStatus.Mp, r["503-2"].Status);
        Assert.Equal("B", r["503-2"].Leg!.CourseCode);          // the closer of the two
        Assert.True(r["503-3"].Leg!.CourseUnassigned);
        Assert.Equal(FinishStatus.Mp, r["503-3"].Status);
        Assert.Equal(FinishDetailKind.CourseNotAssigned, r["503-3"].StatusDetailKind);
        Assert.Null(r["504-2"].Leg!.CourseCode);
        Assert.False(r["504-2"].Leg!.CourseUnassigned);

        // The protocol marks a named course beside the runner, never the one coded with the leg's own number.
        var data = await c.ProtocolDataAsync();
        var doc = Sp.GetRequiredService<IResultProtocolBuilder>().Build(data, new ResultProtocolSettings(),
            Labels.Make<ProtocolLabels>() with { RelayCourseMark = "дистанція {0}" });
        var cells = doc.Sections.SelectMany(x => x.Rows).SelectMany(x => x.Cells).ToList();
        Assert.Contains(cells, cell => cell == "501-1");
        Assert.Contains(cells, cell => cell == "501-2 (дистанція B)");
        Assert.Contains(cells, cell => cell == "502-2 (дистанція C)");
        Assert.DoesNotContain(cells, cell => cell.Contains("дистанція 1"));
    }

    [Fact]
    public async Task RelayTeamsReachTheProtocolTheOnlineSnapshotAndTheTeamsPage()
    {
        var c = await BuildBasicAsync();
        var data = await c.ProtocolDataAsync();
        var group = Assert.Single(data.Groups);
        Assert.NotNull(group.RelayTeams);
        Assert.Equal(6, group.RelayTeams!.Count);
        var winner = group.RelayTeams.Single(t => t.Number == 101);
        Assert.Equal(1, winner.Result!.Place);
        Assert.Equal(3, winner.Legs.Count);
        Assert.Equal("101-2", winner.Legs[1].Runner!.FullName);

        var editor = Sp.GetRequiredService<ICompetitionEditorService>();
        var online = await editor.GetOnlineResultsSnapshotAsync(c.Day(1).Id);
        Assert.Empty(online.Rows);                                   // relay runners never go to the results table
        Assert.Equal(6, online.Teams.Count);
        var onlineWinner = online.Teams.Single(t => t.Number == 101);
        Assert.Equal(1, onlineWinner.Place);
        Assert.Equal(T(1, 5), onlineWinner.Time);
        Assert.Equal("101.3", onlineWinner.Legs[2].Bib);
        Assert.Equal(T(1, 5), onlineWinner.Legs[2].Cumulative);
        Assert.True(online.Teams.Single(t => t.Number == 105).OutOfCompetition);

        var page = await editor.GetRelayTeamsDataAsync();
        Assert.Single(page.Groups);
        Assert.Equal(3, page.Groups[0].LegCount);
        Assert.Equal(6, page.Teams.Count);
        Assert.Equal(17, page.Runners.Count);
        var vacant = page.Teams.Single(t => t.Number == 105).Legs.Single(l => l.Leg == 3);
        Assert.Null(vacant.LinkId);

        var log = await editor.GetFinishReadoutLogAsync();
        Assert.True(log.HasRelayTeams);                              // the log shows the «Команда» column
        var rows = log.Rows;
        var leg2 = rows.Single(x => x.ChipNumber == "1012");
        Assert.Equal(101, leg2.RelayTeamNumber);
        Assert.Equal(2, leg2.RelayLeg);
        Assert.Equal(FinishStatus.Dns, rows.Single(x => x.ChipNumber == "1033").Status);   // leg 2 not read: no start

        // «Група» is the group alone (+ the leg's named course), the team is its own column, the leg is the «Номер».
        var localization = Sp.GetRequiredService<OrientPyx.Localization.ILocalizationService>();
        var cell = new OrientPyx.Presentation.ViewModels.Pages.FinishReadRowViewModel(leg2, localization);
        Assert.Equal(("101.2", "R", "Team 101"), (cell.ParticipantNumber, cell.GroupName, cell.TeamName));
        Assert.Null(cell.StartTimeTooltip);
        var onCourseB = new OrientPyx.Presentation.ViewModels.Pages.FinishReadRowViewModel(
            leg2 with { RelayCourseCode = "B", RelayRestarted = true }, localization);
        Assert.Equal("R · B", onCourseB.GroupName);
        Assert.Equal(localization.Get("FinishRead.Relay.RestartTooltip"), onCourseB.StartTimeTooltip);
    }

    [Fact]
    public async Task RunningTeamsWithCleanLegsComeBeforeWithdrawnOnes()
    {
        var c = await BuildBasicAsync();
        // 108: legs 1–2 done; 109: leg 1 done faster than 103's; 110: nothing read yet.
        var extra = new (int Number, (string Chip, TimeSpan? Finish)[] Legs)[]
        {
            (108, [("1081", T(10, 22)), ("1082", T(10, 50)), ("1083", null)]),
            (109, [("1091", T(10, 15)), ("1092", null), ("1093", null)]),
            (110, [("1101", null), ("1102", null), ("1103", null)])
        };
        foreach (var (number, legs) in extra)
        {
            foreach (var (chip, _) in legs)
                await c.AddRunnerAsync(chip, "", chip, "R");
            await c.AddRelayTeamAsync("R", number, $"Team {number}", 1, legs.Select((l, i) => (l.Chip, i + 1)).ToArray());
            foreach (var (chip, finish) in legs)
                if (finish is { } f)
                    await c.AddReadoutAsync(1, chip, "31 32 33", null, f);
        }

        await c.SelectAsync();
        var editor = Sp.GetRequiredService<ICompetitionEditorService>();
        var online = await editor.GetOnlineResultsSnapshotAsync(c.Day(1).Id);
        // Placed by place → finished «П/К» (105) → running by legs done, then cumulative (108, 109) →
        // nothing done yet (110) → withdrawn (102, MP) → DNS (103: leg 3 read with leg 2 not read).
        Assert.Equal([101, 106, 107, 105, 108, 109, 110, 102, 103], online.Teams.Select(t => t.Number));
    }

    [Fact]
    public async Task RelayRunnersNeverGetSportsRanksButTeamsGetPoints()
    {
        var app = Sp.GetRequiredService<IAppStore>();
        var table = await app.AddPointsRuleAsync(PointsRuleKind.Table);
        table.Name = "relay points"; table.TableJson = "[10,8,6]";
        await app.UpdatePointsRuleAsync(table);

        var c = await BuildBasicAsync();
        await c.UpdateGroupSettingsAsync("R", 1, s => { s.PointsRuleId = table.Id; s.RankLevel = GroupRankLevel.Adult; s.MasterCount = 3; });
        var r = await c.ResultsAsync();
        Assert.Equal(10m, r["101-1"].Points);
        Assert.Equal(10m, r["106-2"].Points);
        Assert.Equal(6m, r["107-3"].Points);
        Assert.Null(r["102-1"].Points);
        Assert.All(r.Values, x => Assert.Null(x.AwardedRank));
    }

    [Fact]
    public async Task TeamNumbersMoveBetweenTeamsInOneWriteAndNeverRepeat()
    {
        var c = await BuildBasicAsync();
        var store = Sp.GetRequiredService<IEventStore>();
        var day = c.Day(1).Id;
        var teams = (await store.GetRelayTeamsAsync(c.Folder, day)).ToDictionary(t => t.Number!.Value);

        // 101 ↔ 102 swap and 103 loses its number — one write, past the (day, number) unique index.
        Assert.True(await store.SetRelayTeamNumbersAsync(c.Folder, day,
            [new(teams[101].Id, 102), new(teams[102].Id, 101), new(teams[103].Id, null)]));
        var after = (await store.GetRelayTeamsAsync(c.Folder, day)).ToDictionary(t => t.Id, t => t.Number);
        Assert.Equal(102, after[teams[101].Id]);
        Assert.Equal(101, after[teams[102].Id]);
        Assert.Null(after[teams[103].Id]);

        // A number another team keeps is refused (Р-20), and nothing of the set is written.
        Assert.False(await store.SetRelayTeamNumbersAsync(c.Folder, day,
            [new(teams[103].Id, 105), new(teams[106].Id, 200)]));
        var unchanged = (await store.GetRelayTeamsAsync(c.Folder, day)).ToDictionary(t => t.Id, t => t.Number);
        Assert.Null(unchanged[teams[103].Id]);
        Assert.Equal(106, unchanged[teams[106].Id]);
    }

    [Fact]
    public async Task LegSplitsSkipTheStartAndFinishBoxesWrittenIntoTheCourse()
    {
        // An OCAD relay course names its start and finish boxes («С1 … Ф1» — Cyrillic, so no S/F marker). The leg is
        // judged OK; its splits on the finish panel and the slip must show every control taken, not all off-course.
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("rsplitsf"), discipline: DisciplineId.Relay);
        var store = Sp.GetRequiredService<IEventStore>();
        var dayId = c.Day(1).Id;
        await c.AddControlsAsync(1, "31", "32", "33");
        await store.AddControlPointsAsync(c.Folder,
        [
            new ControlPoint { EventDayId = dayId, Order = 10, Code = "С1", Type = ControlPointType.Start },
            new ControlPoint { EventDayId = dayId, Order = 11, Code = "Ф1", Type = ControlPointType.Finish },
        ]);
        await c.AddGroupAsync("MixB", "С1 31 32 33 Ф1", s => { s.LegCount = 2; s.MassStartTime = T(10, 0); });
        await c.SetCourseVariantsAsync("MixB", 1, ("AA", "С1 31 32 33 Ф1"), ("AB", "С1 33 32 31 Ф1"));
        await c.SetRelayCourseModeAsync("MixB", 1, RelayCourseMode.ByBib);
        await c.AddRunnerAsync("18-1", "", "181", "MixB");
        await c.AddRunnerAsync("18-2", "", "182", "MixB");
        await c.AddRelayTeamAsync("MixB", 18, "Команда 18", 1, ("18-1", 1), ("18-2", 2));
        await store.UpsertLegCourseAssignmentsAsync(c.Folder, dayId,
        [
            new LegCourseAssignment { EventDayId = dayId, TeamNumber = 18, Leg = 1, CourseKey = "AA" },
            new LegCourseAssignment { EventDayId = dayId, TeamNumber = 18, Leg = 2, CourseKey = "AB" },
        ]);
        await c.AddReadoutAsync(1, "181", "31 32 33", null, T(10, 20));
        // Leg 2's chip read twice: the results use the latest; the earlier one is still judged on the leg's course.
        var earlier = await c.AddReadoutAsync(1, "182", "33 32 31", null, T(10, 10));
        var latest = await c.AddReadoutAsync(1, "182", "33 32 31", null, T(10, 40));

        var r = await c.ResultsAsync();
        Assert.Equal(FinishStatus.Ok, r["18-2"].Status);
        Assert.Equal(FinishStatus.Ok, r["18-1"].Team!.Status);

        var editor = Sp.GetRequiredService<ICompetitionEditorService>();
        foreach (var readout in new[] { latest, earlier })
        {
            var view = await editor.GetFinishSplitsAsync(readout.Id);
            Assert.NotNull(view);
            Assert.Equal("AB", view.VariantCode);
            Assert.Equal(["33", "32", "31"], view.Expected.Select(e => e.Code));
            Assert.All(view.Expected, e => Assert.True(e.Taken));
            Assert.Equal(3, view.VisitedCount);
            Assert.Equal(3, view.ExpectedCount);
            Assert.All(view.Passage.Where(p => p.Kind == PassageKind.Control), p => Assert.True(p.OnCourse));
            // «Правильний порядок» names the course's own start and finish once, as markers (not «S С1 … Ф1 F»).
            Assert.Equal("С1 33 32 31 Ф1", view.PrescribedPattern);
        }

        // Every read-out of the leg runs from the leg's start: leg 1's finish (Р-28).
        var legStart = c.Date + T(10, 20);
        Assert.Equal(legStart, (await editor.GetFinishSplitsAsync(latest.Id))!.Passage[0].Time);
        Assert.Equal(legStart, (await editor.GetFinishSplitsAsync(earlier.Id))!.Passage[0].Time);

        // The slip: no «*» (off-course) row.
        var slip = await editor.GetSplitPrintDocumentAsync(latest.Id);
        Assert.NotNull(slip);
        Assert.Equal("AB", slip.VariantCode);
        Assert.All(slip.Rows, row => Assert.True(row.OnCourse));
    }

    [Fact]
    public async Task AMissedControlOnALegMarksOnlyItselfInTheSplits()
    {
        // A plain leg order is laid out like a set course: missing 32 leaves 33 and 34 on course (the greedy pattern
        // walk used to off-course every control after the miss, and the slip put «*» on all of them).
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("rmiss"), discipline: DisciplineId.Relay);
        await c.AddControlsAsync(1, "S1", "31", "32", "33", "34", "F1");
        await c.AddGroupAsync("R", "S1 31 32 33 34 F1", s => { s.LegCount = 1; s.MassStartTime = T(10, 0); });
        await c.AddRunnerAsync("Miss", "", "5001", "R");
        await c.AddRelayTeamAsync("R", 501, "501", 1, ("Miss", 1));
        var readout = await c.AddReadoutAsync(1, "5001", "31 33 34", null, T(10, 20));

        var r = await c.ResultsAsync();
        Assert.Equal(FinishStatus.Mp, r["Miss"].Status);
        Assert.Equal("32", r["Miss"].StatusDetail);

        var editor = Sp.GetRequiredService<ICompetitionEditorService>();
        var view = await editor.GetFinishSplitsAsync(readout.Id);
        Assert.NotNull(view);
        var controls = view.Passage.Where(p => p.Kind == PassageKind.Control).ToList();
        Assert.All(controls, p => Assert.True(p.OnCourse));
        Assert.Equal(["31", "32", "33", "34"], view.Expected.Select(e => e.Code));
        Assert.Equal([true, false, true, true], view.Expected.Select(e => e.Taken));
        Assert.Equal((3, 4), (view.VisitedCount, view.ExpectedCount));
        Assert.Equal("S1 31 32 33 34 F1", view.PrescribedPattern);
        Assert.NotNull(controls.Single(p => p.Code == "34").Leg);       // 33 → 34 is a whole course leg

        var slip = await editor.GetSplitPrintDocumentAsync(readout.Id);
        Assert.NotNull(slip);
        Assert.All(slip.Rows, row => Assert.True(row.OnCourse));
        Assert.Equal([("31", true), ("32", false), ("33", true), ("34", true)],
            slip.CorrectOrder.Select(x => (x.Code, x.Taken)));
    }
}
