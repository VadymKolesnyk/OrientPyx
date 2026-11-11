using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.BusinessLogic.Services;
using OrientPyx.DataAccess.Persistence;
using OrientPyx.Tests.Support;

namespace OrientPyx.Tests.Core;

/// <summary>
/// OCAD course files with variations handed out to numbers (2.0.3 &lt;TeamCombination&gt;, 3.0
/// &lt;TeamCourseAssignment&gt; / &lt;PersonCourseAssignment&gt;): every variation is parsed, the import makes a relay
/// («за номером етапу» / «набір на команду») or a scatter as chosen, «Варіанти за номерами» lists them, and the
/// removed «за етапом» mode is migrated.
/// </summary>
[Collection(AppCollection.Name)]
public sealed class OcadVariationImportTests
{
    private static IServiceProvider Sp => TestEnvironment.Instance.Services;

    private static TimeSpan T(int h, int m) => new(h, m, 0);

    // A 2-leg relay whose leg-2 variations run the same controls as leg 1's (OCAD names them apart), plus a plain
    // course; two teams handed their variations.
    private const string RelayV2 = """
        <?xml version="1.0" encoding="UTF-8" ?>
        <CourseData>
          <IOFVersion version="2.0.3" />
          <StartPoint><StartPointCode> S1 </StartPointCode><MapPosition x="0" y="0"/></StartPoint>
          <Control><ControlCode> 31 </ControlCode><MapPosition x="10" y="0"/></Control>
          <Control><ControlCode> 32 </ControlCode><MapPosition x="10" y="10"/></Control>
          <FinishPoint><FinishPointCode> F1 </FinishPointCode><MapPosition x="0" y="10"/></FinishPoint>
          <Course>
            <CourseName> МІКС12 </CourseName>
            <CourseId> 5 </CourseId>
            <CourseVariation><CourseVariationId> 0 </CourseVariationId><Name> 1A </Name><StartPointCode> S1 </StartPointCode>
              <CourseControl><Sequence>1</Sequence><ControlCode> 31 </ControlCode></CourseControl>
              <CourseControl><Sequence>2</Sequence><ControlCode> 32 </ControlCode></CourseControl>
              <FinishPointCode> F1 </FinishPointCode></CourseVariation>
            <CourseVariation><CourseVariationId> 1 </CourseVariationId><Name> 1B </Name><StartPointCode> S1 </StartPointCode>
              <CourseControl><Sequence>1</Sequence><ControlCode> 32 </ControlCode></CourseControl>
              <CourseControl><Sequence>2</Sequence><ControlCode> 31 </ControlCode></CourseControl>
              <FinishPointCode> F1 </FinishPointCode></CourseVariation>
            <CourseVariation><CourseVariationId> 2 </CourseVariationId><Name> 2A </Name><StartPointCode> S1 </StartPointCode>
              <CourseControl><Sequence>1</Sequence><ControlCode> 31 </ControlCode></CourseControl>
              <CourseControl><Sequence>2</Sequence><ControlCode> 32 </ControlCode></CourseControl>
              <FinishPointCode> F1 </FinishPointCode></CourseVariation>
            <CourseVariation><CourseVariationId> 3 </CourseVariationId><Name> 2B </Name><StartPointCode> S1 </StartPointCode>
              <CourseControl><Sequence>1</Sequence><ControlCode> 32 </ControlCode></CourseControl>
              <CourseControl><Sequence>2</Sequence><ControlCode> 31 </ControlCode></CourseControl>
              <FinishPointCode> F1 </FinishPointCode></CourseVariation>
          </Course>
          <Course>
            <CourseName> Ч21 </CourseName>
            <CourseId> 6 </CourseId>
            <CourseVariation><CourseVariationId> 0 </CourseVariationId><Name> Ч21 </Name><StartPointCode> S1 </StartPointCode>
              <CourseControl><Sequence>1</Sequence><ControlCode> 31 </ControlCode></CourseControl>
              <FinishPointCode> F1 </FinishPointCode></CourseVariation>
          </Course>
          <TeamCombination>
            <StartNo> 601 </StartNo><CourseId> 5 </CourseId>
            <Leg><LegNo> 1 </LegNo><CourseVariationId> 0 </CourseVariationId><Name> 1A </Name></Leg>
            <Leg><LegNo> 2 </LegNo><CourseVariationId> 3 </CourseVariationId><Name> 2B </Name></Leg>
          </TeamCombination>
          <TeamCombination>
            <StartNo> 602 </StartNo><CourseId> 5 </CourseId>
            <Leg><LegNo> 1 </LegNo><CourseVariationId> 1 </CourseVariationId><Name> 1B </Name></Leg>
            <Leg><LegNo> 2 </LegNo><CourseVariationId> 2 </CourseVariationId><Name> 2A </Name></Leg>
          </TeamCombination>
        </CourseData>
        """;

    // 3.0: a relay family with team assignments and a one-runner family with per-runner assignments.
    private const string AssignmentsV3 = """
        <?xml version="1.0" encoding="UTF-8"?>
        <CourseData xmlns="http://www.orienteering.org/datastandard/3.0" iofVersion="3.0">
          <RaceCourseData>
            <Control><Id>S1</Id></Control><Control><Id>31</Id></Control><Control><Id>32</Id></Control><Control><Id>F1</Id></Control>
            <Course><Name>Ж14_1A</Name><CourseFamily>Ж14</CourseFamily>
              <CourseControl type="Start"><Control>S1</Control></CourseControl>
              <CourseControl type="Control"><Control>31</Control></CourseControl>
              <CourseControl type="Finish"><Control>F1</Control></CourseControl></Course>
            <Course><Name>Ж14_2A</Name><CourseFamily>Ж14</CourseFamily>
              <CourseControl type="Start"><Control>S1</Control></CourseControl>
              <CourseControl type="Control"><Control>32</Control></CourseControl>
              <CourseControl type="Finish"><Control>F1</Control></CourseControl></Course>
            <Course><Name>Ж16_A</Name><CourseFamily>Ж16</CourseFamily>
              <CourseControl type="Start"><Control>S1</Control></CourseControl>
              <CourseControl type="Control"><Control>31</Control></CourseControl>
              <CourseControl type="Control"><Control>32</Control></CourseControl>
              <CourseControl type="Finish"><Control>F1</Control></CourseControl></Course>
            <Course><Name>Ж16_B</Name><CourseFamily>Ж16</CourseFamily>
              <CourseControl type="Start"><Control>S1</Control></CourseControl>
              <CourseControl type="Control"><Control>32</Control></CourseControl>
              <CourseControl type="Control"><Control>31</Control></CourseControl>
              <CourseControl type="Finish"><Control>F1</Control></CourseControl></Course>
            <TeamCourseAssignment><BibNumber>11</BibNumber>
              <TeamMemberCourseAssignment><Leg>1</Leg><CourseName>Ж14_1A</CourseName><CourseFamily>Ж14</CourseFamily></TeamMemberCourseAssignment>
              <TeamMemberCourseAssignment><BibNumber>11-2</BibNumber><CourseName>Ж14_2A</CourseName></TeamMemberCourseAssignment>
            </TeamCourseAssignment>
            <PersonCourseAssignment><BibNumber>21</BibNumber><CourseName>Ж16_B</CourseName><CourseFamily>Ж16</CourseFamily></PersonCourseAssignment>
            <PersonCourseAssignment><BibNumber>22</BibNumber><CourseName>Ж16_A</CourseName></PersonCourseAssignment>
          </RaceCourseData>
        </CourseData>
        """;

    [Fact]
    public void V2_KeepsEveryVariationAndReadsTheTeamCombinations()
    {
        var data = new IofXmlParser().Parse(RelayV2);
        var relay = data.Courses.Single(c => c.Name == "МІКС12");

        // The distinct running orders (scatter) collapse 2A/2B into 1A/1B; every variation is still there by name.
        Assert.Equal(["1A", "1B"], relay.Variants.Select(v => v.Code));
        Assert.Equal(["1A", "1B", "2A", "2B"], relay.AllVariants.Select(v => v.Code));
        Assert.Equal(["S1", "32", "31", "F1"], relay.AllVariants[3].ControlCodes);
        Assert.True(relay.HasVariations);
        Assert.Equal(2, relay.CombinationLegCount);
        Assert.Equal([601, 602], relay.TeamCombinations.Select(t => t.Number));
        Assert.Equal(["1A", "2B"], relay.TeamCombinations[0].Legs.Select(l => l.Code));
        Assert.Equal(["1B", "2A"], relay.TeamCombinations[1].Legs.Select(l => l.Code));

        var plain = data.Courses.Single(c => c.Name == "Ч21");
        Assert.False(plain.HasVariations);
        Assert.Empty(plain.AllVariants);
        Assert.Empty(plain.TeamCombinations);
    }

    [Fact]
    public void V3_ReadsTeamAndPersonCourseAssignments()
    {
        var data = new IofXmlParser().Parse(AssignmentsV3);
        var relay = data.Courses.Single(c => c.Name == "Ж14");
        Assert.Equal(["1A", "2A"], relay.AllVariants.Select(v => v.Code));
        var team = Assert.Single(relay.TeamCombinations);
        Assert.Equal(11, team.Number);
        Assert.Equal([(1, "1A"), (2, "2A")], team.Legs.Select(l => (l.Leg, l.Code)));

        var scatter = data.Courses.Single(c => c.Name == "Ж16");
        Assert.Equal([(21, "B"), (22, "A")], scatter.TeamCombinations.Select(t => (t.Number, t.Legs.Single().Code)));
        Assert.Equal(1, scatter.CombinationLegCount);
    }

    [Fact]
    public async Task RelayByLegNumber_StoresEveryVariationTheLegsAndTheAssignments()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("ocadbib"));
        await c.SelectAsync();
        var editor = Sp.GetRequiredService<ICompetitionEditorService>();
        var data = Use(new IofXmlParser().Parse(RelayV2), "МІКС12", CourseVariationUse.RelayByBib);
        await editor.ImportControlPointsAsync(data, replaceAll: true);
        await editor.ImportGroupsAsync(data, updateExisting: true);

        var row = (await editor.GetGroupDayRowsAsync()).Single(g => g.Name == "МІКС12");
        Assert.Equal(DisciplineId.Relay, row.DisciplineOverride);
        Assert.Equal(2, row.LegCount);
        Assert.Equal(RelayCourseMode.ByBib, row.CourseMode);
        Assert.Equal(["1A", "1B", "2A", "2B"], (await editor.GetCourseVariantsAsync(row.GroupId)).Select(v => v.Code));
        Assert.Null((await editor.GetGroupDayRowsAsync()).Single(g => g.Name == "Ч21").DisciplineOverride);

        // Teams 601 (1A, 2B) and 602 (1B, 2A): 601 runs its own variations; 602's leg 2 runs 32 31 (2B's order) and so
        // mispunches against its 2A.
        await TrackAsync(c, "МІКС12");
        await AddTeamAsync(c, 601, "31 32", "32 31");
        await AddTeamAsync(c, 602, "32 31", "32 31");
        var r = await c.ResultsAsync();
        Assert.Equal(FinishStatus.Ok, r["601-1"].Team!.Status);
        Assert.Equal("2B", r["601-2"].Leg!.CourseCode);
        Assert.Equal(FinishStatus.Ok, r["602-1"].Status);
        Assert.Equal(FinishStatus.Mp, r["602-2"].Status);

        // «Варіанти за номерами»: every leg number with its variation, team and runner.
        await c.SelectAsync();
        var table = await editor.GetNumberVariantsAsync();
        Assert.Equal(["601.1", "601.2", "602.1", "602.2"], table.Rows.Select(x => x.Bib));
        var leg = table.Rows.Single(x => x.Bib == "601.2");
        Assert.Equal(("МІКС12", "2B", "S1 32 31 F1", "601-2", false), (leg.GroupName, leg.Course, leg.CourseOrder, leg.Runner, leg.UnknownCourse));

        // A numbered team with no variations: its legs are listed as missing.
        await c.AddRelayTeamAsync("МІКС12", 603, "603");
        await c.SelectAsync();
        table = await editor.GetNumberVariantsAsync();
        Assert.All(table.Rows.Where(x => x.Number == 603), x => Assert.True(x.Missing));
        Assert.Equal(2, table.Rows.Count(x => x.Number == 603));

        // Re-importing replaces the group's own rows (the file dropped team 602).
        var trimmed = Use(new IofXmlParser().Parse(RelayV2.Replace("<StartNo> 602 </StartNo>", "<StartNo> 0 </StartNo>")),
            "МІКС12", CourseVariationUse.RelayByBib);
        await editor.ImportGroupsAsync(trimmed, updateExisting: true);
        var store = Sp.GetRequiredService<IEventStore>();
        var rows = await store.GetLegCourseAssignmentsAsync(c.Folder, c.Day(1).Id);
        Assert.Equal([601, 601], rows.Select(x => x.TeamNumber));
        Assert.All(rows, x => Assert.Equal(row.GroupId, x.GroupId));
    }

    [Fact]
    public async Task RelayTeamSet_TakesEachTeamsSetFromItsLegNumbers()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("ocadset"));
        await c.SelectAsync();
        var editor = Sp.GetRequiredService<ICompetitionEditorService>();
        var data = Use(new IofXmlParser().Parse(RelayV2), "МІКС12", CourseVariationUse.RelayTeamSet);
        await editor.ImportControlPointsAsync(data, replaceAll: true);
        await editor.ImportGroupsAsync(data, updateExisting: true);
        Assert.Equal(RelayCourseMode.TeamSet, (await editor.GetGroupDayRowsAsync()).Single(g => g.Name == "МІКС12").CourseMode);

        // 602 holds 1B + 2A: its legs swapped the maps (leg 1 ran 2A's order, leg 2 1B's) — the set still closes.
        await TrackAsync(c, "МІКС12");
        await AddTeamAsync(c, 602, "31 32", "32 31");
        var r = await c.ResultsAsync();
        Assert.Equal(FinishStatus.Ok, r["602-1"].Team!.Status);
        Assert.Equal("2A", r["602-1"].Leg!.CourseCode);
        Assert.Equal("1B", r["602-2"].Leg!.CourseCode);

        await c.SelectAsync();
        var teams = await editor.GetRelayTeamsDataAsync();
        Assert.Equal("1B 2A", teams.Teams.Single(t => t.Number == 602).AssignedCourseSet);
    }

    [Fact]
    public async Task Scatter_WithAssignments_KeepsTheFilesCodesForTheNumbers()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("ocadsc"));
        await c.SelectAsync();
        var editor = Sp.GetRequiredService<ICompetitionEditorService>();
        var data = new IofXmlParser().Parse(AssignmentsV3);
        await editor.ImportControlPointsAsync(data, replaceAll: true);
        await editor.ImportGroupsAsync(data, updateExisting: true);

        var zh16 = (await editor.GetGroupDayRowsAsync()).Single(g => g.Name == "Ж16");
        Assert.Equal(DisciplineId.Scatter, zh16.DisciplineOverride);
        await TrackAsync(c, "Ж16");
        await c.AddRunnerAsync("Петренко", "21", "2101", "Ж16");

        await c.SelectAsync();
        var table = await editor.GetNumberVariantsAsync();
        var bib21 = table.Rows.Single(x => x.Number == 21);
        Assert.Equal(("21", "B", "Петренко", "Ж16"), (bib21.Bib, bib21.Course, bib21.Runner, bib21.GroupName));
        Assert.Equal("", table.Rows.Single(x => x.Number == 22).Runner);
    }

    [Fact]
    public void SplitDialog_DefaultsToRelayForAnOcadRelayAndCarriesTheChoice()
    {
        var loc = Sp.GetRequiredService<OrientPyx.Localization.ILocalizationService>();
        var data = new IofXmlParser().Parse(RelayV2);
        var dialog = new OrientPyx.Presentation.ViewModels.Dialogs.SplitGroupsViewModel(loc, new CourseNameSplitter(), data);
        var relay = dialog.Courses.Single(r => r.OriginalName == "МІКС12");
        Assert.True(relay.HasVariations);
        Assert.True(relay.IsRelay);
        Assert.True(relay.IsByBib);
        Assert.False(dialog.Courses.Single(r => r.OriginalName == "Ч21").HasVariations);

        relay.IsTeamSet = true;
        dialog.ConfirmCommand.Execute(null);
        var result = dialog.Completion.Result!;
        Assert.Equal(CourseVariationUse.RelayTeamSet, result.Courses.Single(x => x.Name == "МІКС12").VariationUse);
        Assert.Equal(2, result.Courses.Single(x => x.Name == "МІКС12").TeamCombinations.Count);

        relay.IsScatter = true;
        Assert.Equal(CourseVariationUse.Scatter, relay.VariationUse);
    }

    [Fact]
    public async Task Migration_TurnsTheRemovedByLegModeIntoLegNumberCourses()
    {
        var folder = Path.Combine(Path.GetTempPath(), "orientpyx-tests", "migrate-byleg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var options = new DbContextOptionsBuilder<EventDbContext>()
            .UseSqlite($"Data Source={Path.Combine(folder, "event.db")};Pooling=False")
            .Options;
        var day = Guid.NewGuid();
        var group = Guid.NewGuid();
        // EF keeps a Guid as upper-case text; a raw Guid parameter would bind as a blob.
        static string Text(Guid id) => id.ToString("D").ToUpperInvariant();
        await using (var db = new EventDbContext(options))
        {
            await db.GetService<IMigrator>().MigrateAsync("20261109230217_RelayTeamOptionalNumber");
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "GroupDaySettings" ("Id", "EventDayId", "GroupId", "Order", "CourseOrder", "CourseSetter",
                    "CourseSetterCategory", "RankLevel", "RestartTimesJson", "RestartRanking", "CourseAssignmentMode",
                    "LegCoursesJson", "TeamCourseSet", "CreatedAt", "LegCount")
                VALUES ({0}, {1}, {2}, 1, '', '', '', 'None', '', 'AfterClean', 'ByLeg', '{{"1":"A","2":"B"}}', '',
                    '2026-11-10 00:00:00+00:00', 2);
                """, Text(Guid.NewGuid()), Text(day), Text(group));
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "RelayTeams" ("Id", "EventDayId", "GroupId", "Number", "Name", "OutOfCompetition", "CourseSet", "CreatedAt")
                VALUES ({0}, {1}, {2}, 101, '', 0, '', '2026-11-10 00:00:00+00:00'),
                       ({3}, {1}, {2}, NULL, '', 0, '', '2026-11-10 00:00:00+00:00');
                """, Text(Guid.NewGuid()), Text(day), Text(group), Text(Guid.NewGuid()));
        }

        await using (var db = new EventDbContext(options))
        {
            await db.Database.MigrateAsync();
            Assert.Equal(RelayCourseMode.ByBib, (await db.GroupDaySettings.SingleAsync()).CourseAssignmentMode);
            var rows = await db.LegCourseAssignments.OrderBy(a => a.Leg).ToListAsync();
            Assert.Equal([(101, 1, "A"), (101, 2, "B")], rows.Select(a => (a.TeamNumber, a.Leg, a.CourseKey)));
            Assert.All(rows, a => Assert.Equal((day, (Guid?)group), (a.EventDayId, a.GroupId)));
        }
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
    }

    // The parsed data with one course's variations used as chosen in «Розділення груп».
    private static IofCourseData Use(IofCourseData data, string course, CourseVariationUse use) => new()
    {
        Version = data.Version,
        MapScale = data.MapScale,
        Controls = data.Controls,
        Courses = data.Courses.Select(x => x.Name != course ? x : new IofCourse
        {
            Name = x.Name, Length = x.Length, Climb = x.Climb, ControlCodes = x.ControlCodes, Variants = x.Variants,
            AllVariants = x.AllVariants, TeamCombinations = x.TeamCombinations, VariationUse = use
        }).ToList()
    };

    // Lets the builder address a group the import created.
    private static async Task TrackAsync(CompetitionBuilder c, string name)
    {
        var groups = await Sp.GetRequiredService<IEventStore>().GetGroupsAsync(c.Folder);
        c.Groups[name] = groups.Single(g => g.Name == name);
    }

    // A two-leg team of «МІКС12» with its runners' read-outs (leg 2 starts at leg 1's finish).
    private static async Task AddTeamAsync(CompetitionBuilder c, int number, string leg1, string leg2)
    {
        await c.SetRelaySettingsAsync("МІКС12", 1, 2, T(10, 0));
        await c.AddRunnerAsync($"{number}-1", "", $"{number}1", "МІКС12");
        await c.AddRunnerAsync($"{number}-2", "", $"{number}2", "МІКС12");
        await c.AddRelayTeamAsync("МІКС12", number, $"{number}", 1, ($"{number}-1", 1), ($"{number}-2", 2));
        await c.AddReadoutAsync(1, $"{number}1", leg1, null, T(10, 20));
        await c.AddReadoutAsync(1, $"{number}2", leg2, null, T(10, 40));
    }
}
