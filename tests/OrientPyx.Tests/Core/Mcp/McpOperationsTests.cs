using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models.Mcp;
using OrientPyx.BusinessLogic.Services.Mcp;
using OrientPyx.Tests.Support;

namespace OrientPyx.Tests.Core.Mcp;

/// <summary>The assistant's operations: imports through a preview and a plan, exports, reports and settings.</summary>
[Collection(AppCollection.Name)]
public sealed class McpOperationsTests
{
    private const string IofCourses = """
        <?xml version="1.0" encoding="UTF-8"?>
        <CourseData xmlns="http://www.orienteering.org/datastandard/3.0" iofVersion="3.0">
          <Event><Name>Test</Name></Event>
          <RaceCourseData>
            <Map><Scale>10000</Scale></Map>
            <Control><Id>S1</Id><Position lng="30.500" lat="50.450"/></Control>
            <Control><Id>41</Id><Position lng="30.501" lat="50.450"/></Control>
            <Control><Id>42</Id><Position lng="30.501" lat="50.451"/></Control>
            <Control><Id>F1</Id><Position lng="30.500" lat="50.451"/></Control>
            <Course><Name>Ч18</Name><Length>400</Length>
              <CourseControl type="Start"><Control>S1</Control></CourseControl>
              <CourseControl type="Control"><Control>41</Control></CourseControl>
              <CourseControl type="Control"><Control>42</Control></CourseControl>
              <CourseControl type="Finish"><Control>F1</Control></CourseControl>
            </Course>
          </RaceCourseData>
        </CourseData>
        """;

    private static string TempFile(string name, string content)
    {
        var dir = Path.Combine(TestEnvironment.Instance.DataRoot, "mcp-files", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    [Fact]
    public async Task CoursesAndParticipantsImportThroughAPreviewAndAPlan()
    {
        var c = await McpKit.CompetitionAsync("mcpimp");
        McpKit.Runtime.SetMode(McpMode.AutoChanges);
        try
        {
            var xml = TempFile("courses.xml", IofCourses);
            var preview = await McpKit.CallAsync("import_file", new { competition = c.Summary.Identifier, kind = "iof_courses", path = xml, replace_existing = false });
            Assert.Equal("Ч18", preview.GetProperty("preview").GetProperty("courses")[0].GetProperty("name").GetString());
            Assert.DoesNotContain(await McpKit.Editor.GetControlPointsAsync(), p => p.Code == "41");
            await McpKit.CallAsync("apply_plan", new { plan_id = preview.GetProperty("plan_id").GetString() });
            Assert.Contains(await McpKit.Editor.GetControlPointsAsync(), p => p.Code == "41");
            Assert.Contains(await McpKit.Editor.GetGroupDayRowsAsync(), g => g.Name == "Ч18");

            var csv = TempFile("people.csv", "ПІБ;Група;Рік народження;Клуб\nНовенький Тарас;Ч18;2008;Схід\nКоваль Олег;Ч21;1990;\n");
            var people = await McpKit.CallAsync("import_file", new { competition = c.Summary.Identifier, kind = "participants_table", path = csv });
            var shown = people.GetProperty("preview");
            Assert.Equal(2, shown.GetProperty("rows").GetInt32());
            Assert.Equal("FullName", shown.GetProperty("mapping").GetProperty("ПІБ").GetString());
            Assert.True(shown.GetProperty("added").GetInt32() >= 1);
            await McpKit.CallAsync("apply_plan", new { plan_id = people.GetProperty("plan_id").GetString() });
            var roster = await McpKit.Editor.GetParticipantRosterAsync();
            Assert.Contains(roster, p => p.FullName == "Новенький Тарас" && p.ClubName == "Схід");

            Assert.Contains("not a full path", await McpKit.ErrorAsync("import_file",
                new { competition = c.Summary.Identifier, kind = "uof", path = "relative.xml" }));
            Assert.Contains("takes .xml", await McpKit.ErrorAsync("import_file",
                new { competition = c.Summary.Identifier, kind = "uof", path = csv }));
        }
        finally
        {
            McpKit.Runtime.SetMode(McpMode.ReadOnly);
        }
    }

    [Fact]
    public async Task AProtocolIsExportedToTheDaysExportsFolder()
    {
        var c = await McpKit.CompetitionAsync("mcpexp");
        McpKit.Runtime.SetMode(McpMode.ConfirmChanges);
        try
        {
            var answer = await McpKit.CallAsync("export", new { competition = c.Summary.Identifier, kind = "result_protocol" });
            var path = answer.GetProperty("result").GetProperty("path").GetString()!;
            Assert.True(File.Exists(path));
            Assert.Equal("exports", Path.GetFileName(Path.GetDirectoryName(path)));
            Assert.Empty(McpKit.Ui.Requests);   // a file into the competition folder needs no confirmation
            Checks.IsDocx(File.ReadAllBytes(path));

            Assert.Contains("already exists", await McpKit.ErrorAsync("export", new { competition = c.Summary.Identifier, kind = "result_protocol" }));
            var html = await McpKit.CallAsync("export", new { competition = c.Summary.Identifier, kind = "splits_html", overwrite = true });
            Assert.EndsWith(".html", html.GetProperty("result").GetProperty("path").GetString());
            var xml = await McpKit.CallAsync("export", new { competition = c.Summary.Identifier, kind = "iof_results" });
            Checks.IsIofXml(File.ReadAllBytes(xml.GetProperty("result").GetProperty("path").GetString()!));
        }
        finally
        {
            McpKit.Runtime.SetMode(McpMode.ReadOnly);
        }
    }

    [Fact]
    public async Task ReportsAndSettingsReadAndPatch()
    {
        var c = await McpKit.CompetitionAsync("mcpset");
        foreach (var kind in new[] { "readiness", "dashboard", "map_counts", "finish_log", "start_list", "entry_fees", "summary" })
        {
            var report = await McpKit.CallAsync("get_report", new { kind });
            Assert.Equal(kind, report.GetProperty("report").GetString());
        }
        var results = await McpKit.CallAsync("get_results", new { groups = new[] { "Ч21" } });
        Assert.Equal("Коваль Олег", results.GetProperty("groups")[0].GetProperty("rows")[0].GetProperty("full_name").GetString());

        var settings = await McpKit.CallAsync("get_settings", new { kind = "draw_lanes" });
        Assert.Equal("00:01:00", settings.GetProperty("settings").GetProperty("interval").GetString());

        McpKit.Runtime.SetMode(McpMode.AutoChanges);
        try
        {
            await McpKit.CallAsync("save_settings", new { competition = c.Summary.Identifier, kind = "draw_lanes", patch = new { interval = "00:02:00" } });
            Assert.Single(McpKit.Ui.Requests);  // settings are not undoable: always confirmed
            var after = await McpKit.CallAsync("get_settings", new { kind = "draw_lanes" });
            Assert.Equal("00:02:00", after.GetProperty("settings").GetProperty("interval").GetString());
        }
        finally
        {
            McpKit.Runtime.SetMode(McpMode.ReadOnly);
        }
    }

    [Fact]
    public async Task GroupsControlsAndDirectoriesChangeAndUndo()
    {
        var c = await McpKit.CompetitionAsync("mcpgrp");
        McpKit.Runtime.SetMode(McpMode.AutoChanges);
        try
        {
            var groups = await McpKit.CallAsync("upsert_groups", new
            {
                competition = c.Summary.Identifier,
                groups = new object[]
                {
                    new { name = "Ч21", time_limit_min = 90, course = "31 33" },
                    new { name = "Ж35", course = "34 33", distance_km = 2.5 },
                },
            });
            var rows = await McpKit.Editor.GetGroupDayRowsAsync();
            Assert.Equal(5400, rows.Single(g => g.Name == "Ч21").TimeLimitSeconds);
            Assert.Equal("34 33", rows.Single(g => g.Name == "Ж35").CourseOrder);
            Assert.Contains("merge", await McpKit.ErrorAsync("upsert_groups", new { competition = c.Summary.Identifier, groups = new[] { new { name = "Ч21", new_name = "Ж21" } } }));

            await McpKit.CallAsync("upsert_controls", new { competition = c.Summary.Identifier, controls = new[] { new { code = "99", points = 30 } } });
            Assert.Equal(30, (await McpKit.Editor.GetControlPointsAsync()).Single(p => p.Code == "99").Points);

            await McpKit.CallAsync("manage_directory", new { competition = c.Summary.Identifier, kind = "club", action = "add", items = new[] { new { name = "Карпати" } } });
            Assert.Contains(await McpKit.Editor.GetClubsAsync(), k => k.Name == "Карпати");

            await McpKit.CallAsync("undo", new { step_id = groups.GetProperty("step_id").GetString() });
            rows = await McpKit.Editor.GetGroupDayRowsAsync();
            Assert.Null(rows.Single(g => g.Name == "Ч21").TimeLimitSeconds);
            Assert.DoesNotContain(rows, g => g.Name == "Ж35");
        }
        finally
        {
            McpKit.Runtime.SetMode(McpMode.ReadOnly);
        }
    }
}
