using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Tests.Support;

namespace OrientPyx.Tests.Core;

/// <summary>
/// «Перевірка готовності дня» (<see cref="ICompetitionEditorService.GetDayReadinessAsync"/>): every problem of the day's
/// data in one list — the same checks the cells tint, from one snapshot — and the dashboard's counts of it.
/// </summary>
[Collection(AppCollection.Name)]
public sealed class DayReadinessTests
{
    private static IServiceProvider Sp => TestEnvironment.Instance.Services;

    private static ICompetitionEditorService Editor => Sp.GetRequiredService<ICompetitionEditorService>();

    private static TimeSpan T(int h, int m) => new(h, m, 0);

    private static async Task<DayReadiness> CheckAsync(CompetitionBuilder c, int day = 1)
    {
        await c.SelectAsync(day);
        return await Editor.GetDayReadinessAsync(c.Day(day).Id);
    }

    private static IReadOnlyList<ReadinessIssue> Of(DayReadiness readiness, ReadinessKind kind) =>
        readiness.Issues.Where(i => i.Kind == kind).ToList();

    private static string[] Subjects(DayReadiness readiness, ReadinessKind kind) =>
        Of(readiness, kind).Select(i => i.Subject).Order(StringComparer.Ordinal).ToArray();

    // The chip check follows the local «Тип відмітки»: run it as SportIdent and put the setting back.
    private static async Task WithSportIdentAsync(Func<Task> body)
    {
        var settings = Sp.GetRequiredService<IAppSettingsService>();
        var original = await settings.GetReadoutTypeAsync();
        await settings.SaveReadoutTypeAsync(ReadoutType.SportIdent);
        try
        {
            await body();
        }
        finally
        {
            await settings.SaveReadoutTypeAsync(original);
        }
    }

    [Fact]
    public async Task ACleanDay_HasNoProblems()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("ready-clean"));
        await c.AddControlsAsync(1, "S1", "31", "32", "F1");
        await c.AddGroupAsync("Ч21", "S1 31 32 F1");
        await c.AddRunnerAsync("Ann", "1", "8100001", "Ч21", T(10, 0));
        await c.AddRunnerAsync("Bob", "2", "8100002", "Ч21", T(10, 2));

        var readiness = await CheckAsync(c);

        Assert.Empty(readiness.Issues);
        Assert.Equal(c.Day(1).Id, readiness.DayId);
        Assert.Equal(1, readiness.DayNumber);
        var dashboard = await Editor.GetDashboardAsync();
        Assert.Equal(0, dashboard.ReadinessErrors);
        Assert.Equal(0, dashboard.ReadinessWarnings);
    }

    [Fact]
    public async Task UnknownDay_IsEmpty()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("ready-unknown"));
        await c.SelectAsync();

        Assert.Same(DayReadiness.Empty, await Editor.GetDayReadinessAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Participants_AreChecked_AndTheDashboardCountsTheSame() => await WithSportIdentAsync(async () =>
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("ready-people"));
        // 28 controls: an SI-Card 5 (30 punches) has no room for the 2 spare ones.
        var codes = Enumerable.Range(31, 28).Select(n => n.ToString()).ToArray();
        await c.AddControlsAsync(1, codes);
        await c.AddGroupAsync("Ч21", string.Join(' ', codes));
        await c.AddGroupAsync("Ж16", "31 32");
        var store = Sp.GetRequiredService<IEventStore>();
        await store.UpdateGroupAgeWindowAsync(c.Folder, c.Groups["Ж16"].Id, 2010, null);
        // A group that is not on the day.
        var away = new Group { Name = "Інша" };
        await store.AddGroupAsync(c.Folder, away);

        var start = T(10, 0);
        await c.AddRunnerAsync("NoGroup", "1", "8100001", null, start);
        await c.AddRunnerAsync("Away", "2", "8100002", "Ч21", start, configureDay: l => l.GroupId = away.Id);
        await c.AddRunnerAsync("NoChip", "3", "", "Ч21", start);
        await c.AddRunnerAsync("Dup1", "4", "8100100", "Ч21", start);
        await c.AddRunnerAsync("Dup2", "5", "8100100", "Ч21", start);
        await c.AddRunnerAsync("Small", "6", "123456", "Ч21", start);
        await c.AddRunnerAsync("Old", "7", "8100007", "Ж16", start);       // born 1990+, the group allows 2010 and later
        await c.AddRunnerAsync("Num1", "77", "8100008", "Ч21", start);
        await c.AddRunnerAsync("Num2", "77", "8100009", "Ч21", start);
        await c.AddRunnerAsync("Field", "10", "8100010", "Ч21", start);

        await c.SelectAsync();
        var field = await Editor.AddCustomFieldAsync("Стать", CustomFieldKind.Choice, [new ChoiceOption("Ч"), new ChoiceOption("Ж")]);
        Assert.NotNull(field);
        await store.SetParticipantFieldValueAsync(c.Folder, c.Runners["Field"].Id, field.Key, "X");

        var readiness = await CheckAsync(c);

        Assert.Equal(["Away", "NoGroup"], Subjects(readiness, ReadinessKind.NoGroup));
        Assert.All(Of(readiness, ReadinessKind.NoGroup), i => Assert.Equal(ReadinessSeverity.Error, i.Severity));
        Assert.Equal(["NoChip"], Subjects(readiness, ReadinessKind.NoChip));
        Assert.Equal(ReadinessSeverity.Warning, Of(readiness, ReadinessKind.NoChip)[0].Severity);
        Assert.Equal(["Dup1", "Dup2"], Subjects(readiness, ReadinessKind.DuplicateChip));
        Assert.Equal(["Small"], Subjects(readiness, ReadinessKind.ChipTooSmall));
        Assert.Equal("Participants.ChipCapacity.TooSmall", Of(readiness, ReadinessKind.ChipTooSmall)[0].MessageKey);
        var age = Assert.Single(Of(readiness, ReadinessKind.BirthYearOutOfGroup));
        Assert.Equal("Old", age.Subject);
        Assert.Equal("Participants.AgeViolation.TooOld", age.MessageKey);
        Assert.Equal(["Ж16", "2010"], age.Args);
        Assert.Equal(["Num1", "Num2"], Subjects(readiness, ReadinessKind.DuplicateNumber));
        var invalid = Assert.Single(Of(readiness, ReadinessKind.InvalidFieldValue));
        Assert.Equal("Field", invalid.Subject);
        Assert.Equal(field.Key, invalid.FieldKey);
        Assert.Equal(ReadinessField.CustomField, invalid.Field);
        // Every runner has a start: nothing about start times.
        Assert.Empty(Of(readiness, ReadinessKind.NoStartTime));
        Assert.Empty(Of(readiness, ReadinessKind.DayWithoutStarts));
        // Each participant issue names the row to show.
        Assert.All(readiness.Issues.Where(i => i.Target == ReadinessTarget.Participant), i => Assert.NotNull(i.LinkId));
        // Errors first.
        Assert.Equal(readiness.Issues.OrderBy(i => i.Severity).Select(i => i.Key), readiness.Issues.Select(i => i.Key));

        // The dashboard counts the same list; «Без групи» as the day table shows it (none set, or not on the day).
        var dashboard = await Editor.GetDashboardAsync();
        Assert.Equal(readiness.Errors, dashboard.ReadinessErrors);
        Assert.Equal(readiness.Warnings, dashboard.ReadinessWarnings);
        Assert.Equal(2, dashboard.WithoutGroup);
    });

    [Fact]
    public async Task Groups_AreChecked_OnlyWhenTheyHaveMembers()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("ready-groups"));
        var controls = await c.AddControlsAsync(1, "31", "32", "33");
        await c.AddGroupAsync("Empty", "");
        await c.AddGroupAsync("Rogaine", "", s => s.DisciplineOverride = DisciplineId.Rogaine);
        await c.AddGroupAsync("BadCode", "S1 31 32 F");
        await c.AddGroupAsync("Mixed", "31 [2 31 32]", s => s.DisciplineOverride = DisciplineId.Mixed);
        await c.AddGroupAsync("Scatter", "", s => s.DisciplineOverride = DisciplineId.Scatter);
        await c.SetCourseVariantsAsync("Scatter", 1, ("A", "31 32"), ("B", "32 31 S9"));
        await c.AddGroupAsync("PerLeg", "", s =>
        {
            s.DisciplineOverride = DisciplineId.Relay;
            s.LegCount = 2;
            s.MassStartTime = T(10, 0);
        });
        await c.SetRelayCourseModeAsync("PerLeg", 1, RelayCourseMode.PerLeg);
        await c.SetLegCoursesAsync("PerLeg", 1, ("A", 1, "31 32"));
        await c.AddGroupAsync("Disabled", "31 32 33");
        await c.AddGroupAsync("Plugin", "31 32", s => s.DisciplineOverride = new DisciplineId("missing.plugin"));
        await c.AddGroupAsync("Unused", "");   // nobody runs it: nothing to report

        foreach (var g in new[] { "Empty", "Rogaine", "BadCode", "Mixed", "Scatter", "Disabled", "Plugin" })
            await c.AddRunnerAsync($"{g}-1", "", $"81{g.Length}{g[0]}", g, T(10, 0));
        await c.AddRunnerAsync("Leg1", "", "8200001", "PerLeg");
        await c.AddRunnerAsync("Leg2", "", "8200002", "PerLeg");
        await c.AddRelayTeamAsync("PerLeg", 1, "Т1", 1, ("Leg1", 1), ("Leg2", 2));

        // A control the judge switched off is the judge's decision, not a data problem.
        var store = Sp.GetRequiredService<IEventStore>();
        await store.SetControlPointsDisabledAsync(c.Folder, c.Day(1).Id, [controls.Single(p => p.Code == "33").Id]);

        var readiness = await CheckAsync(c);

        Assert.Equal(["Empty", "Rogaine"], Subjects(readiness, ReadinessKind.GroupWithoutCourse));
        var unknown = Of(readiness, ReadinessKind.UnknownCourseCodes);
        Assert.Equal(["S1, F"], unknown.Single(i => i.Subject == "BadCode").Args);
        // A scatter group's variants are checked one by one.
        Assert.Equal(["S9"], unknown.Single(i => i.Subject == "Scatter · B").Args);
        var pattern = Assert.Single(Of(readiness, ReadinessKind.InvalidCoursePattern));
        Assert.Equal("Mixed", pattern.Subject);
        Assert.StartsWith("CoursePattern.Error.", pattern.MessageKey);
        var leg = Assert.Single(Of(readiness, ReadinessKind.RelayLegWithoutVariant));
        Assert.Equal(["2"], leg.Args);
        var plugin = Assert.Single(Of(readiness, ReadinessKind.UnknownDiscipline));
        Assert.Equal(ReadinessTarget.Group, plugin.Target);
        Assert.Equal(["missing.plugin", "missing"], plugin.Args);
        Assert.DoesNotContain(readiness.Issues, i => i.GroupName is "Disabled" or "Unused" || i.Subject is "Disabled" or "Unused");
        Assert.All(readiness.Issues.Where(i => i.Target == ReadinessTarget.Group), i => Assert.NotNull(i.GroupSettingsId));
    }

    [Fact]
    public async Task UnknownDayDiscipline_IsOneProblemOfTheDay()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("ready-plugin"), discipline: new DisciplineId("missing.plugin"));
        await c.AddControlsAsync(1, "31", "32");
        await c.AddGroupAsync("Ч21", "31 32");
        await c.AddRunnerAsync("Ann", "1", "8100001", "Ч21", T(10, 0));

        var readiness = await CheckAsync(c);

        var issue = Assert.Single(readiness.Issues);
        Assert.Equal(ReadinessKind.UnknownDiscipline, issue.Kind);
        Assert.Equal(ReadinessTarget.Day, issue.Target);
        Assert.Equal(c.Day(1).Id, issue.DayId);
        Assert.Equal("Banner.MissingPlugin", issue.MessageKey);
    }

    [Fact]
    public async Task StartTimes_OneLineForADayWithoutAny_ElsePerGroupAndPerRunner()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("ready-starts"));
        await c.AddControlsAsync(1, "31", "32");
        await c.AddGroupAsync("A", "31 32");
        await c.AddGroupAsync("B", "31 32");
        await c.AddGroupAsync("R", "31 32", s => { s.DisciplineOverride = DisciplineId.Relay; s.LegCount = 2; });
        foreach (var name in new[] { "A1", "A2", "A3" })
            await c.AddRunnerAsync(name, "", $"81000{name[1]}", "A");
        await c.AddRunnerAsync("B1", "", "8100021", "B");
        await c.AddRunnerAsync("R1", "", "8100031", "R");
        await c.AddRunnerAsync("R2", "", "8100032", "R");
        await c.AddRelayTeamAsync("R", 101, "Т101", 1, ("R1", 1), ("R2", 2));

        // Nobody has a start time (no draw yet, or a start station): one line for the day.
        var readiness = await CheckAsync(c);
        var day = Assert.Single(Of(readiness, ReadinessKind.DayWithoutStarts));
        Assert.Equal(ReadinessTarget.Draw, day.Target);
        Assert.Equal(["5"], day.Args);                       // A×3, B, and the relay's first leg
        Assert.Empty(Of(readiness, ReadinessKind.GroupWithoutStarts));
        Assert.Empty(Of(readiness, ReadinessKind.NoStartTime));
        Assert.Empty(Of(readiness, ReadinessKind.RelayNoMassStart));

        // Part of the day is drawn: per group and per runner.
        await c.UpdateLinkAsync("A1", 1, l => l.StartTime = T(10, 0));
        await c.UpdateLinkAsync("A2", 1, l => l.StartTime = T(10, 1));
        readiness = await CheckAsync(c);
        Assert.Empty(Of(readiness, ReadinessKind.DayWithoutStarts));
        Assert.Equal(["A3"], Subjects(readiness, ReadinessKind.NoStartTime));
        Assert.Equal(["B"], Subjects(readiness, ReadinessKind.GroupWithoutStarts));
        var relay = Assert.Single(Of(readiness, ReadinessKind.RelayNoMassStart));
        Assert.Equal("R", relay.Subject);
        Assert.Equal(ReadinessTarget.MassStart, relay.Target);

        // The relay's mass start covers its first legs; a later leg starts at the handover and is never checked.
        await c.SetRelaySettingsAsync("R", 1, 2, T(11, 0));
        readiness = await CheckAsync(c);
        Assert.Empty(Of(readiness, ReadinessKind.RelayNoMassStart));
        Assert.DoesNotContain(readiness.Issues, i => i.Subject is "R1" or "R2");
    }

    [Fact]
    public async Task RelayTeams_AreChecked_AndLegNumbersAgreeWithTheParticipantTable()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("ready-relay"), discipline: DisciplineId.Relay);
        await c.AddControlsAsync(1, "31", "32");
        await c.AddGroupAsync("R", "31 32", s => { s.LegCount = 3; s.MassStartTime = T(10, 0); });
        await c.AddGroupAsync("N", "31 32", s => { s.LegCount = 2; s.MassStartTime = T(10, 0); });
        await c.SetCourseVariantsAsync("N", 1, ("A", "31 32"), ("B", "32 31"));
        await c.SetRelayCourseModeAsync("N", 1, RelayCourseMode.ByBib);

        // «R»: 101 has two of three legs and an extra runner with no leg; a team without a number.
        await c.AddRunnerAsync("101-1", "", "8101011", "R");
        await c.AddRunnerAsync("101-2", "", "8101012", "R");
        var team101 = await c.AddRelayTeamAsync("R", 101, "Т101", 1, ("101-1", 1), ("101-2", 2));
        await c.AddRunnerAsync("101-x", "", "8101019", "R", configureDay: l => { l.RelayTeamId = team101.Id; l.Leg = null; });
        var store = Sp.GetRequiredService<IEventStore>();
        var dayId = c.Day(1).Id;
        var unnumbered = new RelayTeam { EventDayId = dayId, GroupId = c.Groups["R"].Id, Number = null, Name = "Без номера" };
        await store.AddRelayTeamsAsync(c.Folder, [unnumbered]);

        // «N» takes its courses by leg number: 201.1 has one, 201.2 none.
        await c.AddRunnerAsync("201-1", "", "8102011", "N");
        await c.AddRunnerAsync("201-2", "", "8102012", "N");
        await c.AddRelayTeamAsync("N", 201, "Т201", 1, ("201-1", 1), ("201-2", 2));
        await store.UpsertLegCourseAssignmentsAsync(c.Folder, dayId,
        [
            new LegCourseAssignment { EventDayId = dayId, TeamNumber = 201, Leg = 1, CourseKey = "A", GroupId = c.Groups["N"].Id },
        ]);

        var readiness = await CheckAsync(c);

        var incomplete = Of(readiness, ReadinessKind.RelayTeamIncomplete);
        Assert.Equal(["2", "3"], incomplete.Single(i => i.RelayTeamId == team101.Id).Args);
        Assert.Contains(incomplete, i => i.RelayTeamId == unnumbered.Id);     // an empty team too
        Assert.Equal(unnumbered.Id, Assert.Single(Of(readiness, ReadinessKind.RelayTeamWithoutNumber)).RelayTeamId);
        var withoutLeg = Assert.Single(Of(readiness, ReadinessKind.RelayRunnerWithoutLeg));
        Assert.Equal("101-x", withoutLeg.Subject);
        Assert.Equal(team101.Id, withoutLeg.RelayTeamId);
        Assert.Equal(ReadinessSeverity.Error, withoutLeg.Severity);

        // №17 is exactly the participant table's red group cell.
        var rows = await Editor.GetParticipantDayRowsAsync();
        var flagged = rows.Where(r => r.Relay?.CourseIssue is not null).Select(r => r.LinkId).Order().ToArray();
        Assert.Equal(flagged, Of(readiness, ReadinessKind.RelayLegCourse).Select(i => i.LinkId!.Value).Order().ToArray());
        Assert.Equal(["201-2"], Subjects(readiness, ReadinessKind.RelayLegCourse));
        // …and «Варіанти за номерами» does not repeat it.
        Assert.Empty(Of(readiness, ReadinessKind.NumberWithoutVariant));
    }

    [Fact]
    public async Task NumberVariants_WrongGroupAndScatterNumbersWithoutAMap_AsOnThePage()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("ready-numbers"), discipline: DisciplineId.Scatter);
        await c.AddControlsAsync(1, "31", "32");
        await c.AddGroupAsync("S", "");
        await c.AddGroupAsync("T", "");
        await c.SetCourseVariantsAsync("S", 1, ("A", "31 32"), ("B", "32 31"));
        await c.SetCourseVariantsAsync("T", 1, ("A", "31 32"), ("B", "32 31"));
        await c.AddRunnerAsync("S11", "11", "8100011", "S", T(10, 0));
        await c.AddRunnerAsync("S12", "12", "8100012", "S", T(10, 1));
        await c.AddRunnerAsync("S13", "13", "8100013", "S", T(10, 2));
        await c.AddRunnerAsync("T21", "", "8100021", "T", T(10, 3));   // no number: needs no map by number
        var store = Sp.GetRequiredService<IEventStore>();
        var dayId = c.Day(1).Id;
        await store.UpsertLegCourseAssignmentsAsync(c.Folder, dayId,
        [
            new LegCourseAssignment { EventDayId = dayId, TeamNumber = 11, Leg = 1, CourseKey = "A", GroupId = c.Groups["S"].Id },
            // Given in «T», while runner 12 runs in «S»: the results never apply it.
            new LegCourseAssignment { EventDayId = dayId, TeamNumber = 12, Leg = 1, CourseKey = "B", GroupId = c.Groups["T"].Id },
        ]);
        // A read-out of a chip nobody has is the finish desk's business, not the day's readiness.
        await c.AddReadoutAsync(1, "999999", "31 32", null, T(10, 40));

        var readiness = await CheckAsync(c);

        var wrong = Assert.Single(Of(readiness, ReadinessKind.NumberVariantWrongGroup));
        Assert.Equal(12, wrong.TeamNumber);
        Assert.Equal(ReadinessTarget.NumberVariants, wrong.Target);
        Assert.Equal("NumberVariants.GroupMismatch.Scatter", wrong.MessageKey);
        Assert.Equal(["T", "S"], wrong.Args);
        Assert.Equal([13], Of(readiness, ReadinessKind.NumberWithoutVariant).Select(i => i.TeamNumber!.Value));

        // The same rows the «Варіанти за номерами» page shows red.
        var page = await Editor.GetNumberVariantsAsync();
        Assert.Equal(page.Rows.Where(r => r.GroupMismatch).Select(r => r.Number).Order(),
            Of(readiness, ReadinessKind.NumberVariantWrongGroup).Select(i => i.TeamNumber!.Value).Order());
        Assert.Equal(page.Unassigned.Select(r => r.Number).Order(),
            Of(readiness, ReadinessKind.NumberWithoutVariant).Select(i => i.TeamNumber!.Value).Order());
        Assert.DoesNotContain(readiness.Issues, i => i.Args.Contains("999999"));
    }

    [Fact]
    public async Task GroupsForDay_CarryNamedCoursesAndTheirOwnDaysDiscipline() => await WithSportIdentAsync(async () =>
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("ready-groupsforday"), dayCount: 2);
        await c.SetDisciplineAsync(2, DisciplineId.Scatter);
        var codes = Enumerable.Range(31, 28).Select(n => n.ToString()).ToArray();
        await c.AddControlsAsync(2, codes);
        await c.AddGroupAsync("S", "31");
        // Day 2's longest variant has 28 controls although the group's own order has one.
        await c.SetCourseVariantsAsync("S", 2, ("A", "31"), ("B", string.Join(' ', codes)));
        await c.AddRunnerAsync("Small", "1", "123456", "S", T(10, 0));

        await c.SelectAsync(1);   // the session is on day 1
        var day2 = Assert.Single(await Editor.GetGroupsForDayAsync(c.Day(2).Id));
        Assert.Equal(DisciplineId.Scatter, day2.DayDefaultDiscipline);
        Assert.Equal(28, day2.ScatterMaxControlCount);
        var strategy = Sp.GetRequiredService<IDisciplineStrategyProvider>().For(DisciplineId.Scatter);
        Assert.Equal(28, ChipCapacity.NeededPunches(day2, strategy));
        Assert.Equal(DisciplineId.SetCourse, Assert.Single(await Editor.GetGroupsForDayAsync(c.Day(1).Id)).DayDefaultDiscipline);

        // The readiness check judges the chip by the same longest course.
        var readiness = await CheckAsync(c, day: 2);
        Assert.Equal(["Small"], Subjects(readiness, ReadinessKind.ChipTooSmall));
    });

    [Theory]
    [InlineData("S1 31 F", "31", "S1, F")]
    [InlineData("[2: 31 32] 33", "31 32 33", "")]          // the any-of count is not a code
    [InlineData("S1 99 F", "", "")]                         // no КП on the day yet: nothing to check against
    [InlineData("31 m31", "31 M31", "")]                    // look-alike letters fold (TextKey)
    public void UnknownCodes_AreTheCodesTheDayHasNoControlFor(string course, string dayCodes, string expected)
    {
        var day = GroupCourseChecks.DayCodes(dayCodes.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(code => new ControlPoint { Code = code }));

        Assert.Equal(expected, string.Join(", ", GroupCourseChecks.UnknownCodes(course, day)));
    }
}
