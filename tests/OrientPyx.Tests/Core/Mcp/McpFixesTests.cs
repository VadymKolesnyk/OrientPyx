using System.Text;
using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.BusinessLogic.Models.Mcp;
using OrientPyx.Tests.Support;

namespace OrientPyx.Tests.Core.Mcp;

/// <summary>The fixes after the assistant's full test run (docs/mcp-test-report-2026-11-27.md) and the fourth mode.</summary>
[Collection(AppCollection.Name)]
public sealed class McpFixesTests
{
    [Fact]
    public async Task NoConfirmationRunsARiskyChangeWithoutAsking()
    {
        var c = await McpKit.CompetitionAsync("mcpnoconf");
        McpKit.Runtime.SetMode(McpMode.NoConfirmation);
        try
        {
            var context = await McpKit.CallAsync("get_context");
            Assert.Equal("no_confirmation", context.GetProperty("mode").GetString());
            await McpKit.CallAsync("update_competition", new { competition = c.Summary.Identifier, fields = new { jury = "Журі" } });
            Assert.Empty(McpKit.Ui.Requests);
            Assert.Equal("Журі", (await McpKit.Editor.GetInfoAsync())!.Jury);
        }
        finally
        {
            McpKit.Runtime.SetMode(McpMode.ReadOnly);
        }
    }

    [Fact]
    public async Task TakingSomeoneOffTheirOnlyDaySaysItDeletesThem()
    {
        var c = await McpKit.CompetitionAsync("mcponlyday");
        McpKit.Runtime.SetMode(McpMode.AutoChanges);
        try
        {
            var answer = await McpKit.CallAsync("update_participants", new
            {
                competition = c.Summary.Identifier,
                changes = new object[] { new { participant = "Ткач Ольга", day = 1, fields = new { in_day = false } } },
            });
            var result = answer.GetProperty("result");
            Assert.Equal("Ткач Ольга", result.GetProperty("deleted_from_competition")[0].GetString());
            Assert.Contains(result.GetProperty("warnings").EnumerateArray(), w => w.GetString()!.Contains("deletes them"));
            Assert.DoesNotContain(await McpKit.Editor.GetParticipantRosterAsync(), p => p.FullName == "Ткач Ольга");

            await McpKit.CallAsync("undo", new { step_id = answer.GetProperty("step_id").GetString() });
            Assert.Contains(await McpKit.Editor.GetParticipantRosterAsync(), p => p.FullName == "Ткач Ольга");
            Assert.Contains("already undone", await McpKit.ErrorAsync("undo", new { step_id = answer.GetProperty("step_id").GetString() }));

            var removed = await McpKit.CallAsync("remove_participants", new { competition = c.Summary.Identifier, participants = new[] { "Ткач Ольга" }, mode = "from_day" });
            Assert.Equal("Ткач Ольга", removed.GetProperty("result").GetProperty("deleted_from_competition")[0].GetString());
        }
        finally
        {
            McpKit.Runtime.SetMode(McpMode.ReadOnly);
        }
    }

    [Fact]
    public async Task ResultsComeInTheProtocolsOrderAndTheLimitKeepsTheLeaders()
    {
        await McpKit.CompetitionAsync("mcporder");
        // Alphabetically Бондар (2nd) comes first; the protocol puts Коваль (1st) first.
        var results = await McpKit.CallAsync("get_results", new { groups = new[] { "Ч21" }, limit_per_group = 1 });
        var row = results.GetProperty("groups")[0].GetProperty("rows")[0];
        Assert.Equal("Коваль Олег", row.GetProperty("full_name").GetString());
        Assert.Equal(1, row.GetProperty("place").GetInt32());
    }

    [Fact]
    public async Task SettingsRefuseTheOnlineSwitchUnknownKeysAndBadTimes()
    {
        var c = await McpKit.CompetitionAsync("mcpsettings");
        McpKit.Runtime.SetMode(McpMode.NoConfirmation);
        try
        {
            Assert.Contains("only in OrientPyx", await McpKit.ErrorAsync("save_settings",
                new { competition = c.Summary.Identifier, kind = "online_publish", patch = new { enabled = true } }));
            Assert.Contains("Unknown setting 'no_such_key'", await McpKit.ErrorAsync("save_settings",
                new { competition = c.Summary.Identifier, kind = "iof_export", patch = new { no_such_key = 1 } }));
            Assert.Contains("HH:mm:ss", await McpKit.ErrorAsync("save_settings",
                new { competition = c.Summary.Identifier, kind = "draw_lanes", patch = new { interval = "abc" } }));
            await McpKit.CallAsync("save_settings", new { competition = c.Summary.Identifier, kind = "draw_lanes", patch = new { interval = "00:02:00" } });

            // The summary of a competition with no template of its own counts every day, as the page opens it.
            var summary = await McpKit.CallAsync("get_settings", new { kind = "summary_protocol" });
            Assert.Equal(1, summary.GetProperty("settings").GetProperty("days").GetArrayLength());
        }
        finally
        {
            McpKit.Runtime.SetMode(McpMode.ReadOnly);
        }
    }

    [Fact]
    public async Task AControlKeepsItsMapPosition()
    {
        var c = await McpKit.CompetitionAsync("mcpmap");
        McpKit.Runtime.SetMode(McpMode.AutoChanges);
        try
        {
            await McpKit.CallAsync("upsert_controls", new { competition = c.Summary.Identifier, controls = new[] { new { code = "31", map_x = 12.5, map_y = 7.0 } } });
            var point = (await McpKit.Editor.GetControlPointsAsync()).Single(p => p.Code == "31");
            Assert.Equal(12.5, point.MapX);
            Assert.Equal(7.0, point.MapY);
        }
        finally
        {
            McpKit.Runtime.SetMode(McpMode.ReadOnly);
        }
    }

    [Fact]
    public async Task ADayScopedImportPreviewShowsWhatItWillDo()
    {
        var c = await McpKit.CompetitionAsync("mcpdayimp");
        McpKit.Runtime.SetMode(McpMode.AutoChanges);
        try
        {
            var csv = TempFile("day.csv", "ПІБ;Група;Рік народження\nКоваль Олег;Ч21;1990\nНовий Учасник;Ж21;2001\n");
            var plan = await McpKit.CallAsync("import_file", new
            {
                competition = c.Summary.Identifier, kind = "participants_table", path = csv, scope = "current_day", link_field = "full_name",
            });
            var preview = plan.GetProperty("preview");
            Assert.Equal(1, preview.GetProperty("added").GetInt32());
            Assert.Equal(1, preview.GetProperty("updated").GetInt32());
        }
        finally
        {
            McpKit.Runtime.SetMode(McpMode.ReadOnly);
        }
    }

    [Fact]
    public void ANamesakeIsPickedByBirthDateThenClub()
    {
        var clubA = Guid.NewGuid();
        var older = new Participant { FullName = "Тестенко Андрій", BirthDate = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero) };
        var younger = new Participant { FullName = "Тестенко Андрій", BirthDate = new DateTimeOffset(1990, 3, 1, 0, 0, 0, TimeSpan.Zero), ClubId = clubA };
        IReadOnlyList<Participant> both = [older, younger];
        Assert.Same(younger, ImportNameMatch.Pick(both, new DateTimeOffset(1990, 3, 1, 0, 0, 0, TimeSpan.Zero), null, p => p.BirthDate, p => p.ClubId));
        Assert.Same(younger, ImportNameMatch.Pick(both, null, clubA, p => p.BirthDate, p => p.ClubId));
        Assert.True(ImportNameMatch.IsGuess(both, null, null, p => p.BirthDate, p => p.ClubId));
        Assert.False(ImportNameMatch.IsGuess(both, new DateTimeOffset(1970, 6, 1, 0, 0, 0, TimeSpan.Zero), null, p => p.BirthDate, p => p.ClubId));
    }

    [Fact]
    public async Task AReadOutWithoutAFinishAfterAnAssignedStartIsDnfNotDns()
    {
        var c = await McpKit.CompetitionAsync("mcpdnf");
        await c.AddRunnerAsync("Без Фінішу", "4", "5004", "Ч21", CompetitionBuilder.At(10, 5));
        var day = c.Days[0];
        var date = c.Date;
        var store = McpKit.Sp.GetRequiredService<IEventStore>();
        await store.AddFinishReadoutsAsync(c.Folder,
        [
            new FinishReadout
            {
                EventDayId = day.Id, Order = 99, ChipNumber = "5004", Punches = "31 32",
                PunchTimes = $"31@{(date + CompetitionBuilder.At(10, 15)).UtcTicks},32@{(date + CompetitionBuilder.At(10, 25)).UtcTicks}",
                ContentKey = c.Summary.Identifier + "-5004-nofinish", CreatedAt = date + CompetitionBuilder.At(10, 40),
            },
        ]);
        await c.SelectAsync(1);
        var runner = (await McpKit.Editor.GetParticipantRosterAsync()).Single(p => p.FullName == "Без Фінішу");
        Assert.Equal(FinishStatus.Dnf, runner.Days[0].Result.Status);
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
