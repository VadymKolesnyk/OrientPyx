using System.Text;
using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Models.Mcp;
using OrientPyx.Tests.Support;

namespace OrientPyx.Tests.Core.Mcp;

/// <summary>The tools added after the full test run: the journal with redo, the background status, the read-out edit,
/// relay deletes, duplicates and merge, a look into a file, groups by rules, plugin report export.</summary>
[Collection(AppCollection.Name)]
public sealed class McpNewToolsTests
{
    [Fact]
    public async Task TheJournalListsAChangeThatIsUndoneAndRedone()
    {
        var c = await McpKit.CompetitionAsync("mcpredo");
        McpKit.Runtime.SetMode(McpMode.AutoChanges);
        try
        {
            Assert.Contains("no undone change", await McpKit.ErrorAsync("redo"));
            var answer = await McpKit.CallAsync("update_participants", new
            {
                competition = c.Summary.Identifier,
                changes = new[] { new { participant = "Коваль Олег", fields = new { note = "перевірити" } } },
            });
            var step = answer.GetProperty("step_id").GetString();
            string Note() => McpKit.Editor.GetParticipantRosterAsync().Result.Single(p => p.FullName == "Коваль Олег").Note;

            var list = await McpKit.CallAsync("list_changes");
            var entry = list.GetProperty("changes")[0];
            Assert.Equal(step, entry.GetProperty("step_id").GetString());
            Assert.Equal("done", entry.GetProperty("state").GetString());
            Assert.True(entry.GetProperty("can_undo").GetBoolean());
            Assert.Contains("перевірити", entry.GetProperty("details")[0].GetString());

            await McpKit.CallAsync("undo");
            Assert.Equal(string.Empty, Note());
            entry = (await McpKit.CallAsync("list_changes")).GetProperty("changes")[0];
            Assert.Equal("undone", entry.GetProperty("state").GetString());
            Assert.True(entry.GetProperty("can_redo").GetBoolean());
            // The undo itself is not a change of its own in the journal.
            Assert.Equal(1, (await McpKit.CallAsync("list_changes")).GetProperty("total").GetInt32());

            var redone = await McpKit.CallAsync("redo");
            Assert.True(redone.GetProperty("result").GetProperty("redone").GetBoolean());
            Assert.Equal("перевірити", Note());
            Assert.Contains("not undone", await McpKit.ErrorAsync("redo", new { step_id = step }));

            // The original step id still takes it back after the redo.
            await McpKit.CallAsync("undo", new { step_id = step });
            Assert.Equal(string.Empty, Note());
        }
        finally
        {
            McpKit.Runtime.SetMode(McpMode.ReadOnly);
        }
    }

    [Fact]
    public async Task TheBackgroundStatusShowsTheProcessesAndTheFinishDesk()
    {
        var c = await McpKit.CompetitionAsync("mcpbg");
        await c.AddReadoutAsync(1, "9999", "31 32 33", CompetitionBuilder.At(11, 0), CompetitionBuilder.At(11, 30));
        McpKit.Ui.Activities.Add(new McpActivityInfo("Зчитування чіпів", "Файл rentchip.csv недоступний", "failing", false, true));
        var status = await McpKit.CallAsync("get_background_status");
        var activity = status.GetProperty("activities")[0];
        Assert.Equal("Зчитування чіпів", activity.GetProperty("title").GetString());
        Assert.Equal("failing", activity.GetProperty("state").GetString());
        Assert.True(activity.GetProperty("problem").GetBoolean());
        var readouts = status.GetProperty("readouts");
        Assert.Equal(5, readouts.GetProperty("read_outs").GetInt32());
        Assert.Equal(1, readouts.GetProperty("unknown_chips").GetInt32());
        Assert.Equal("9999", readouts.GetProperty("last_read").GetProperty("chip").GetString());
    }

    [Fact]
    public async Task AnUnknownChipGoesToARunnerAStatusIsSetByHandAndBothUndo()
    {
        var c = await McpKit.CompetitionAsync("mcpreadout");
        await c.AddReadoutAsync(1, "7777", "31 34 33", CompetitionBuilder.At(10, 4), CompetitionBuilder.At(10, 44));
        McpKit.Runtime.SetMode(McpMode.AutoChanges);
        try
        {
            var id = c.Summary.Identifier;
            var given = await McpKit.CallAsync("edit_readout", new { competition = id, readout = "7777", participant = "Ткач Ольга" });
            Assert.Equal("Ткач Ольга", given.GetProperty("result").GetProperty("participant").GetString());
            Assert.Equal("7777", McpKit.Editor.GetParticipantRosterAsync().Result.Single(p => p.FullName == "Ткач Ольга").Days[0].Chip);

            // «Бонус» counts only where «Бали» are scored.
            Assert.Contains("score points", await McpKit.ErrorAsync("edit_readout", new { competition = id, readout = "5001", bonus = 2 }));

            // A chip someone holds is taken only when asked to.
            Assert.Contains("take_chip=true", await McpKit.ErrorAsync("edit_readout", new { competition = id, readout = "5001", participant = "Бондар Іван" }));

            var status = await McpKit.CallAsync("edit_readout", new { competition = id, readout = "5003", status = "OK" });
            Assert.Equal("OK", status.GetProperty("result").GetProperty("status").GetString());
            Assert.Equal(FinishStatus.Ok, McpKit.Editor.GetParticipantRosterAsync().Result.Single(p => p.FullName == "Шевченко Петро").Days[0].Result.Status);
            await McpKit.CallAsync("undo", new { step_id = status.GetProperty("step_id").GetString() });
            Assert.Equal(FinishStatus.Mp, McpKit.Editor.GetParticipantRosterAsync().Result.Single(p => p.FullName == "Шевченко Петро").Days[0].Result.Status);

            await McpKit.CallAsync("undo", new { step_id = given.GetProperty("step_id").GetString() });
            Assert.Equal("5012", McpKit.Editor.GetParticipantRosterAsync().Result.Single(p => p.FullName == "Ткач Ольга").Days[0].Chip);

            // A walk-up: the read-out makes a new participant of the day.
            await c.AddReadoutAsync(1, "8888", "31 32 33", CompetitionBuilder.At(10, 6), CompetitionBuilder.At(10, 36));
            await McpKit.CallAsync("edit_readout", new
            {
                competition = id, readout = "8888", new_participant = new { full_name = "Новенький Тест", group = "Ч21", birth_date = "2001-05-02" },
            });
            var walkUp = McpKit.Editor.GetParticipantRosterAsync().Result.Single(p => p.FullName == "Новенький Тест");
            Assert.Equal("8888", walkUp.Days[0].Chip);
            Assert.Equal("Ч21", walkUp.Days[0].GroupName);
        }
        finally
        {
            McpKit.Runtime.SetMode(McpMode.ReadOnly);
        }
    }

    [Fact]
    public async Task ARelayRunnerIsReleasedAndATeamDeletedAndBothUndo()
    {
        var c = await CompetitionBuilder.CreateAsync(McpKit.Sp, TestIds.Next("mcprelaydel"));
        await c.AddControlsAsync(1, "31", "32");
        await c.AddGroupAsync("Е21", "31 32", s =>
        {
            s.DisciplineOverride = DisciplineId.Relay;
            s.LegCount = 2;
            s.MassStartTime = CompetitionBuilder.At(10, 0);
        });
        await c.AddRunnerAsync("Етап Перший", "", "7101", "Е21");
        await c.AddRunnerAsync("Етап Другий", "", "7102", "Е21");
        await c.AddRelayTeamAsync("Е21", 1, "Т1", 1, ("Етап Перший", 1), ("Етап Другий", 2));
        await c.SelectAsync(1);
        McpKit.Ui.Reset();
        McpKit.Runtime.SetMode(McpMode.AutoChanges);
        try
        {
            var id = c.Summary.Identifier;
            var released = await McpKit.CallAsync("manage_relay", new { competition = id, action = "release_runner", participant = "Етап Другий" });
            Assert.Null(McpKit.Editor.GetParticipantRosterAsync().Result.Single(p => p.FullName == "Етап Другий").Days[0].Relay);
            Assert.Contains("on no relay team", await McpKit.ErrorAsync("manage_relay", new { competition = id, action = "release_runner", participant = "Етап Другий" }));

            var deleted = await McpKit.CallAsync("manage_relay", new { competition = id, action = "delete_team", team = "1" });
            Assert.Empty((await McpKit.Editor.GetRelayTeamsDataAsync()).Teams);
            Assert.Contains(await McpKit.Editor.GetParticipantRosterAsync(), p => p.FullName == "Етап Перший" && p.Days[0].IsMember);

            await McpKit.CallAsync("undo", new { step_id = deleted.GetProperty("step_id").GetString() });
            await McpKit.CallAsync("undo", new { step_id = released.GetProperty("step_id").GetString() });
            var team = Assert.Single((await McpKit.Editor.GetRelayTeamsDataAsync()).Teams);
            Assert.Equal(2, team.Legs.Count(l => l.ParticipantId is not null));
        }
        finally
        {
            McpKit.Runtime.SetMode(McpMode.ReadOnly);
        }
    }

    [Fact]
    public async Task DuplicatesAreFoundAndMergedAndTheMergeUndoes()
    {
        var c = await McpKit.CompetitionAsync("mcpdup", days: 2);
        var born = new DateTimeOffset(1990, 1, 15, 0, 0, 0, CompetitionBuilder.Offset);
        // The same person typed again for day 2 (lower case, a double space), with a ФСОУ code the first lacks.
        await c.AddRunnerAsync("коваль  олег", "", "6001", "Ч21", configure: p =>
        {
            p.BirthDate = born;
            p.FsouCode = "UA-777";
        }, days: [2]);
        // Typo and a namesake by another order.
        await c.AddRunnerAsync("Шевченко Петрo", "", "6002", "Ч21", days: [2]);
        var roster = await McpKit.Editor.GetParticipantRosterAsync();
        var keep = roster.Single(p => p.FullName == "Коваль Олег");
        var merge = roster.Single(p => p.FullName == "коваль  олег");
        // Коваль Олег runs both days already: make the second entry the only one on day 2.
        await McpKit.Editor.RemoveParticipantFromDayAsync(keep.Days.Single(d => d.DayNumber == 2).LinkId!.Value, keep.ParticipantId);

        var found = await McpKit.CallAsync("find_duplicates");
        var pairs = found.GetProperty("candidates").EnumerateArray().ToList();
        Assert.Contains(pairs, p => p.GetProperty("reasons").EnumerateArray().Any(r => r.GetString() == "same_name")
            && new[] { p.GetProperty("a").GetProperty("id").GetString(), p.GetProperty("b").GetProperty("id").GetString() }.Order()
                .SequenceEqual(new[] { keep.ParticipantId.ToString(), merge.ParticipantId.ToString() }.Order()));

        McpKit.Runtime.SetMode(McpMode.AutoChanges);
        try
        {
            var merged = await McpKit.CallAsync("merge_participants", new
            {
                competition = c.Summary.Identifier,
                merges = new[] { new { keep = keep.ParticipantId.ToString(), merge = merge.ParticipantId.ToString() } },
            });
            var after = await McpKit.Editor.GetParticipantRosterAsync();
            Assert.DoesNotContain(after, p => p.ParticipantId == merge.ParticipantId);
            var kept = after.Single(p => p.ParticipantId == keep.ParticipantId);
            Assert.Equal("6001", kept.Days.Single(d => d.DayNumber == 2).Chip);
            Assert.True(kept.Days.Single(d => d.DayNumber == 1).IsMember);
            Assert.Equal("UA-777", kept.FsouCode);
            Assert.Equal(2, merged.GetProperty("result").GetProperty("merges")[0].GetProperty("days_moved")[0].GetInt32());

            await McpKit.CallAsync("undo", new { step_id = merged.GetProperty("step_id").GetString() });
            var back = await McpKit.Editor.GetParticipantRosterAsync();
            Assert.Contains(back, p => p.ParticipantId == merge.ParticipantId && p.Days.Single(d => d.DayNumber == 2).Chip == "6001");
            Assert.Equal(string.Empty, back.Single(p => p.ParticipantId == keep.ParticipantId).FsouCode);
        }
        finally
        {
            McpKit.Runtime.SetMode(McpMode.ReadOnly);
        }
    }

    [Fact]
    public async Task AFileIsLookedIntoBeforeTheImport()
    {
        await McpKit.CompetitionAsync("mcpinspect");
        var csv = TempFile("entries.csv", "ПІБ;Група;Рік народження;Щось\nКоваль Олег;Ч21;1990;x\nНовий Учасник;Ж21;2001;y\n");
        var table = (await McpKit.CallAsync("inspect_file", new { path = csv })).GetProperty("details");
        Assert.Equal("participants_table", table.GetProperty("import_kind").GetString());
        Assert.Equal(2, table.GetProperty("rows").GetInt32());
        Assert.Equal("FullName", table.GetProperty("mapping").GetProperty("ПІБ").GetString());
        Assert.Contains(table.GetProperty("unmapped_columns").EnumerateArray(), x => x.GetString() == "Щось");

        var xml = TempFile("courses.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <CourseData xmlns="http://www.orienteering.org/datastandard/3.0" iofVersion="3.0">
              <RaceCourseData>
                <Control><Id>31</Id></Control>
                <Control><Id>32</Id></Control>
                <Course><Name>A</Name><CourseControl><Control>31</Control></CourseControl></Course>
              </RaceCourseData>
            </CourseData>
            """);
        var courses = (await McpKit.CallAsync("inspect_file", new { path = xml })).GetProperty("details");
        Assert.Equal("iof_courses", courses.GetProperty("import_kind").GetString());
        Assert.Equal(2, courses.GetProperty("controls").GetInt32());
        Assert.Equal(1, courses.GetProperty("courses").GetInt32());

        Assert.Contains("not a full path", await McpKit.ErrorAsync("inspect_file", new { path = "entries.csv" }));
    }

    [Fact]
    public async Task GroupsAreGivenByRulesWithAPreviewAndUndo()
    {
        var c = await McpKit.CompetitionAsync("mcprules");
        await c.AddGroupAsync("Ч35", "31 32 33");
        await c.AddRunnerAsync("Старший Без Групи", "41", "6101", null, configure: p => p.BirthDate = new DateTimeOffset(1980, 2, 2, 0, 0, 0, CompetitionBuilder.Offset));
        await c.AddRunnerAsync("Молодший Без Групи", "42", "6102", null, configure: p => p.BirthDate = new DateTimeOffset(2000, 2, 2, 0, 0, 0, CompetitionBuilder.Offset));
        McpKit.Runtime.SetMode(McpMode.AutoChanges);
        try
        {
            // The rule takes the group's own age window («Ч35»: born 1990 or earlier).
            await McpKit.CallAsync("upsert_groups", new { competition = c.Summary.Identifier, groups = new[] { new { name = "Ч35", max_birth_year = 1990 } } });
            Assert.Contains("No additional field 'nope'", await McpKit.ErrorAsync("assign_groups", new
            {
                competition = c.Summary.Identifier,
                rules = new object[] { new { group = "Ч21", where = new Dictionary<string, string> { ["field.nope"] = "x" } } },
            }));
            var plan = await McpKit.CallAsync("assign_groups", new
            {
                competition = c.Summary.Identifier,
                rules = new object[] { new { group = "Ч35" }, new { group = "Ч21", use_age_window = false } },
            });
            var preview = plan.GetProperty("preview");
            Assert.Equal(2, preview.GetProperty("changes").GetInt32());
            Assert.Equal(1, preview.GetProperty("rules")[0].GetProperty("people").GetInt32());

            var applied = await McpKit.CallAsync("apply_plan", new { plan_id = plan.GetProperty("plan_id").GetString() });
            var roster = await McpKit.Editor.GetParticipantRosterAsync();
            Assert.Equal("Ч35", roster.Single(p => p.FullName == "Старший Без Групи").Days[0].GroupName);
            Assert.Equal("Ч21", roster.Single(p => p.FullName == "Молодший Без Групи").Days[0].GroupName);

            await McpKit.CallAsync("undo", new { step_id = applied.GetProperty("step_id").GetString() });
            Assert.Null((await McpKit.Editor.GetParticipantRosterAsync()).Single(p => p.FullName == "Старший Без Групи").Days[0].GroupId);
        }
        finally
        {
            McpKit.Runtime.SetMode(McpMode.ReadOnly);
        }
    }

    [Fact]
    public async Task APluginReportExportNamesTheReportsOrSaysThereAreNone()
    {
        var c = await McpKit.CompetitionAsync("mcpplugrep");
        McpKit.Runtime.SetMode(McpMode.AutoChanges);
        try
        {
            var error = await McpKit.ErrorAsync("export", new { competition = c.Summary.Identifier, kind = "plugin_report" });
            Assert.True(error.Contains("No switched-on plugin has reports") || error.Contains("'report' is required"), error);
            Assert.Contains("No report 'nope'", await McpKit.ErrorAsync("export", new { competition = c.Summary.Identifier, kind = "plugin_report", report = "nope" }));
        }
        finally
        {
            McpKit.Runtime.SetMode(McpMode.ReadOnly);
        }
    }

    private static string TempFile(string name, string content)
    {
        var dir = Path.Combine(TestEnvironment.Instance.DataRoot, "mcp-files", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }
}
