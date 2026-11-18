using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Tests.Support;

namespace OrientPyx.Tests.Core;

/// <summary>
/// A relay group «за номером етапу» (Р-41): the participant tables flag a runner whose leg number has no course the results
/// can use — none given, given in another group, a code the group has no course for, or a team without a number — by the
/// same rule the results look the course up with. Other modes, and runners without a leg, are never flagged.
/// </summary>
[Collection(AppCollection.Name)]
public sealed class RelayLegCourseIssueTests
{
    private static IServiceProvider Sp => TestEnvironment.Instance.Services;

    private static TimeSpan T(int h, int m) => new(h, m, 0);

    [Fact]
    public async Task LegNumbersWithoutAUsableCourseAreFlaggedInBothParticipantTables()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("rlegissue"), discipline: DisciplineId.Relay);
        await c.AddControlsAsync(1, "31", "32");
        foreach (var g in new[] { "R", "Q", "G" })
        {
            await c.AddGroupAsync(g, "31 32", s => { s.LegCount = 2; s.MassStartTime = T(10, 0); });
            await c.SetCourseVariantsAsync(g, 1, ("A", "31 32"), ("B", "32 31"));
        }
        await c.SetRelayCourseModeAsync("R", 1, RelayCourseMode.ByBib);
        await c.SetRelayCourseModeAsync("Q", 1, RelayCourseMode.ByBib);
        // «G» runs the group's own course on every leg: nothing to look up by number.

        async Task<RelayTeam> Team(string group, int number)
        {
            await c.AddRunnerAsync($"{number}-1", "", $"{number}1", group);
            await c.AddRunnerAsync($"{number}-2", "", $"{number}2", group);
            return await c.AddRelayTeamAsync(group, number, $"Т{number}", 1, ($"{number}-1", 1), ($"{number}-2", 2));
        }
        var team501 = await Team("R", 501);
        await Team("R", 502);
        await Team("G", 601);
        // A third runner on 501 is on the team without a leg.
        await c.AddRunnerAsync("501-3", "", "5013", "R", configureDay: l => { l.RelayTeamId = team501.Id; l.Leg = null; });

        var store = Sp.GetRequiredService<IEventStore>();
        var dayId = c.Day(1).Id;
        // A team of «R» without a number.
        await c.AddRunnerAsync("unnumbered-1", "", "7001", "R");
        var unnumbered = new RelayTeam { EventDayId = dayId, GroupId = c.Groups["R"].Id, Number = null, Name = "Без номера" };
        await store.AddRelayTeamsAsync(c.Folder, [unnumbered]);
        var links = await store.GetParticipantDaysAsync(c.Folder, dayId);
        await store.AssignRelayLegAsync(c.Folder, unnumbered.Id, 1,
            links.Single(l => l.ParticipantId == c.Runners["unnumbered-1"].Id).Id);

        await store.UpsertLegCourseAssignmentsAsync(c.Folder, dayId,
        [
            new LegCourseAssignment { EventDayId = dayId, TeamNumber = 501, Leg = 1, CourseKey = "A", GroupId = c.Groups["R"].Id },
            // Given in «Q»: the results never apply it to a team of «R».
            new LegCourseAssignment { EventDayId = dayId, TeamNumber = 501, Leg = 2, CourseKey = "B", GroupId = c.Groups["Q"].Id },
            // 502.1 has none; 502.2 names a code «R» has no course for.
            new LegCourseAssignment { EventDayId = dayId, TeamNumber = 502, Leg = 2, CourseKey = "Z" },
        ]);

        await c.SelectAsync();
        var editor = Sp.GetRequiredService<ICompetitionEditorService>();
        var rows = await editor.GetParticipantDayRowsAsync();
        RelayLegCourseIssue? Issue(string runner) => rows.Single(r => r.FullName == runner).Relay?.CourseIssue;

        Assert.Null(Issue("501-1"));
        Assert.Equal(new RelayLegCourseIssue(RelayLegCourseIssueKind.OtherGroup, "R", "501.2", "Q"), Issue("501-2"));
        Assert.Null(Issue("501-3"));                                                        // no leg
        Assert.Equal(new RelayLegCourseIssue(RelayLegCourseIssueKind.NotAssigned, "R", "502.1"), Issue("502-1"));
        Assert.Equal(new RelayLegCourseIssue(RelayLegCourseIssueKind.UnknownCourse, "R", "502.2", "Z"), Issue("502-2"));
        Assert.Equal(RelayLegCourseIssueKind.NoTeamNumber, Issue("unnumbered-1")?.Kind);
        Assert.Null(Issue("601-1"));                                                        // the group's own course
        Assert.Null(Issue("601-2"));

        // The roster carries the same per day.
        var roster = await editor.GetParticipantRosterAsync();
        Assert.Equal(RelayLegCourseIssueKind.OtherGroup,
            roster.Single(r => r.FullName == "501-2").Days.Single().Relay?.CourseIssue?.Kind);
        Assert.Null(roster.Single(r => r.FullName == "501-1").Days.Single().Relay?.CourseIssue);

        // The results agree: the flagged legs, once read, run with no course.
        await c.AddReadoutAsync(1, "5011", "31 32", null, new TimeSpan(10, 20, 0));
        await c.AddReadoutAsync(1, "5012", "32 31", null, new TimeSpan(10, 40, 0));
        await c.AddReadoutAsync(1, "5021", "31 32", null, new TimeSpan(10, 20, 0));
        var results = await c.ResultsAsync();
        Assert.False(results["501-1"].Team!.Legs.Single(l => l.Leg == 1).CourseUnassigned);
        Assert.True(results["501-2"].Team!.Legs.Single(l => l.Leg == 2).CourseUnassigned);
        Assert.True(results["502-1"].Team!.Legs.Single(l => l.Leg == 1).CourseUnassigned);

        // Moved to the team's group, the variation counts: the runner is no longer flagged.
        await editor.SetLegCoursesAsync([new LegCourseEntry(501, 2, "B", c.Groups["R"].Id)]);
        rows = await editor.GetParticipantDayRowsAsync();
        Assert.Null(Issue("501-2"));
    }
}
