using System.IO.Pipelines;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.BusinessLogic.Models.Mcp;
using OrientPyx.BusinessLogic.Services;
using OrientPyx.BusinessLogic.Services.Mcp;
using OrientPyx.DataAccess.Mcp;
using OrientPyx.Tests.Support;

namespace OrientPyx.Tests.Core.Mcp;

/// <summary>Shared helpers of the assistant tests: a small competition with results, calls through the dispatcher.</summary>
internal static class McpKit
{
    public static IServiceProvider Sp => TestEnvironment.Instance.Services;
    public static McpToolDispatcher Dispatcher => Sp.GetRequiredService<McpToolDispatcher>();
    public static McpRuntime Runtime => Sp.GetRequiredService<McpRuntime>();
    public static TestMcpUi Ui => Sp.GetRequiredService<TestMcpUi>();
    public static ICompetitionEditorService Editor => Sp.GetRequiredService<ICompetitionEditorService>();

    /// <summary>Two groups, five runners, read-outs: one MP, one without a read-out.</summary>
    public static async Task<CompetitionBuilder> CompetitionAsync(string prefix, int days = 1)
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next(prefix), dayCount: days);
        foreach (var d in c.Days)
            await c.AddControlsAsync(d.Number, "31", "32", "33", "34");
        await c.AddGroupAsync("Ч21", "31 32 33");
        await c.AddGroupAsync("Ж21", "31 34 33");
        await c.AddRunnerAsync("Коваль Олег", "1", "5001", "Ч21", CompetitionBuilder.At(10, 0));
        await c.AddRunnerAsync("Бондар Іван", "2", "5002", "Ч21", CompetitionBuilder.At(10, 1));
        await c.AddRunnerAsync("Шевченко Петро", "3", "5003", "Ч21", CompetitionBuilder.At(10, 2));
        await c.AddRunnerAsync("Мельник Анна", "11", "5011", "Ж21", CompetitionBuilder.At(10, 3));
        await c.AddRunnerAsync("Ткач Ольга", "12", "5012", "Ж21", CompetitionBuilder.At(10, 4));
        await c.AddReadoutAsync(1, "5001", "31 32 33", CompetitionBuilder.At(10, 0), CompetitionBuilder.At(10, 30));
        await c.AddReadoutAsync(1, "5002", "31 32 33", CompetitionBuilder.At(10, 1), CompetitionBuilder.At(10, 41));
        await c.AddReadoutAsync(1, "5003", "31 33", CompetitionBuilder.At(10, 2), CompetitionBuilder.At(10, 25));
        await c.AddReadoutAsync(1, "5011", "31 34 33", CompetitionBuilder.At(10, 3), CompetitionBuilder.At(10, 50));
        await c.SelectAsync(1);
        Ui.Reset();
        return c;
    }

    public static async Task<JsonElement> CallAsync(string tool, object? args = null)
    {
        var result = await Dispatcher.CallAsync(tool, McpArgs.Parse(JsonSerializer.Serialize(args ?? new { })), McpCall.None, CancellationToken.None);
        Assert.False(result.IsError, $"{tool}: {result.Text}");
        return JsonDocument.Parse(result.Text).RootElement.Clone();
    }

    public static async Task<string> ErrorAsync(string tool, object? args = null)
    {
        var result = await Dispatcher.CallAsync(tool, McpArgs.Parse(JsonSerializer.Serialize(args ?? new { })), McpCall.None, CancellationToken.None);
        Assert.True(result.IsError, $"{tool} was expected to fail: {result.Text}");
        return result.Text;
    }

    public static async Task<(string[] Columns, List<JsonElement[]> Rows)> QueryAsync(string sql)
    {
        var answer = await CallAsync("query", new { sql });
        var columns = answer.GetProperty("columns").EnumerateArray().Select(c => c.GetString()!).ToArray();
        var rows = answer.GetProperty("rows").EnumerateArray().Select(r => r.EnumerateArray().ToArray()).ToList();
        return (columns, rows);
    }
}

[Collection(AppCollection.Name)]
public sealed class McpPolicyTests
{
    [Fact]
    public void EveryToolOfTheCatalogueHasAnImplementation()
    {
        var implemented = McpKit.Dispatcher.Implemented.ToHashSet();
        var catalogue = McpToolCatalog.All.Select(t => t.Name).ToList();
        Assert.Equal(catalogue.Count, catalogue.Distinct().Count());
        Assert.Empty(catalogue.Where(n => !implemented.Contains(n)));
        Assert.Empty(implemented.Where(n => McpToolCatalog.Find(n) is null));
        foreach (var t in McpToolCatalog.All)
        {
            Assert.Equal("object", t.InputSchema["type"]?.GetValue<string>());
            Assert.False(string.IsNullOrWhiteSpace(t.Description), t.Name);
        }
    }

    [Fact]
    public void ReadOnlyListsOnlyTheReadingTools()
    {
        var runtime = McpKit.Runtime;
        try
        {
            runtime.SetMode(McpMode.ReadOnly);
            var listed = McpKit.Dispatcher.ListedTools();
            Assert.All(listed, t => Assert.Equal(McpToolClass.Read, t.Class));
            Assert.Equal(16, listed.Count);
            runtime.SetMode(McpMode.ConfirmChanges);
            Assert.Equal(McpToolCatalog.All.Count, McpKit.Dispatcher.ListedTools().Count);
        }
        finally
        {
            runtime.SetMode(McpMode.ReadOnly);
        }
    }

    [Fact]
    public void TheVerdictFollowsTheClassAndTheMode()
    {
        Assert.Equal(McpVerdict.Allow, McpToolPolicy.Decide(McpToolClass.Read, McpMode.ReadOnly));
        Assert.Equal(McpVerdict.Deny, McpToolPolicy.Decide(McpToolClass.Write, McpMode.ReadOnly));
        Assert.Equal(McpVerdict.Confirm, McpToolPolicy.Decide(McpToolClass.Write, McpMode.ConfirmChanges));
        Assert.Equal(McpVerdict.Allow, McpToolPolicy.Decide(McpToolClass.Write, McpMode.AutoChanges));
        Assert.Equal(McpVerdict.Confirm, McpToolPolicy.Decide(McpToolClass.Risky, McpMode.AutoChanges));
        Assert.Equal(McpVerdict.Allow, McpToolPolicy.Decide(McpToolClass.Risky, McpMode.ConfirmChanges, allowedForSession: true));
        Assert.Equal(McpVerdict.Allow, McpToolPolicy.Decide(McpToolClass.File, McpMode.ConfirmChanges));
        // «Без підтвердження»: nothing waits, a risky change included; reading-only still refuses.
        Assert.Equal(McpVerdict.Allow, McpToolPolicy.Decide(McpToolClass.Write, McpMode.NoConfirmation));
        Assert.Equal(McpVerdict.Allow, McpToolPolicy.Decide(McpToolClass.Risky, McpMode.NoConfirmation));
        Assert.True(McpToolPolicy.IsListed(McpToolClass.Risky, McpMode.NoConfirmation));
        Assert.Equal(McpToolClass.Risky, McpToolPolicy.BySize(McpToolClass.Write, McpToolPolicy.RiskyDeletes + 1, 0));
        Assert.Equal(McpToolClass.Risky, McpToolPolicy.BySize(McpToolClass.Write, 0, McpToolPolicy.RiskyUpdates + 1));
        Assert.Equal(McpToolClass.Write, McpToolPolicy.BySize(McpToolClass.Write, McpToolPolicy.RiskyDeletes, McpToolPolicy.RiskyUpdates));
    }

    [Fact]
    public void ThingsDoneOnlyInTheProgramAreListedAndNotTools()
    {
        string[] never =
        [
            "delete_competition", "rename_identifier", "delete_day", "delete_all_participants", "clear_readouts", "unlock_day",
            "print", "online_publishing", "network_roles", "app_settings", "points_and_ranks_tables", "plugins",
        ];
        Assert.Equal(never.Order(), McpToolPolicy.NeverViaMcp.Keys.Order());
        Assert.All(never, n => Assert.Null(McpToolCatalog.Find(n)));
    }

    [Fact]
    public async Task AWriteToolIsRefusedInReadOnly()
    {
        var c = await McpKit.CompetitionAsync("mcpro");
        McpKit.Runtime.SetMode(McpMode.ReadOnly);
        var error = await McpKit.ErrorAsync("update_participants", new
        {
            competition = c.Summary.Identifier,
            changes = new[] { new { participant = "1", fields = new { note = "x" } } },
        });
        Assert.Contains("Лише читання", error);
    }
}

[Collection(AppCollection.Name)]
public sealed class McpEditorScopeTests
{
    [Fact]
    public async Task TheScopePinsTheCompetitionAndDayWithoutTouchingTheSession()
    {
        var a = await CompetitionBuilder.CreateAsync(McpKit.Sp, TestIds.Next("scopea"), dayCount: 2);
        await a.AddGroupAsync("Ч21", "31");
        var b = await CompetitionBuilder.CreateAsync(McpKit.Sp, TestIds.Next("scopeb"));
        await b.AddGroupAsync("Ж10", "31");
        await b.AddGroupAsync("Ж12", "31");
        await a.SelectAsync(1);
        var session = McpKit.Sp.GetRequiredService<ISessionService>();

        // Pinned on another competition, the call names that competition's day (it never borrows the session's).
        using (EditorScope.Enter(b.Summary, b.Day(1)))
        {
            Assert.Equal(["Ж10", "Ж12"], (await McpKit.Editor.GetGroupDayRowsAsync()).Select(g => g.Name).Order());
            Assert.Single(await McpKit.Editor.GetDaysAsync());
        }
        using (EditorScope.Enter(a.Summary, a.Day(2)))
        {
            var added = await McpKit.Editor.AddGroupToDayAsync("Ч35");
            Assert.Contains((await McpKit.Sp.GetRequiredService<IEventStore>().GetGroupDaySettingsAsync(a.Folder, a.Day(2).Id)), s => s.GroupId == added.GroupId);
        }

        Assert.Equal(a.Summary.Identifier, session.CurrentEvent!.Identifier);
        Assert.Equal(a.Day(1).Id, session.CurrentDay!.Id);
        Assert.Equal(["Ч21"], (await McpKit.Editor.GetGroupDayRowsAsync()).Select(g => g.Name));
    }
}

[Collection(AppCollection.Name)]
public sealed class McpAnalyticsTests
{
    [Fact]
    public async Task TheResultsTableHoldsExactlyTheProtocolsResults()
    {
        var c = await McpKit.CompetitionAsync("mcpres");
        var expected = await c.ResultsAsync(1);
        var (columns, rows) = await McpKit.QueryAsync(
            "SELECT p.full_name, r.status, r.place, r.time_s, r.time_text FROM results r JOIN participants p USING (participant_id) WHERE r.day = 1");
        Assert.Equal(["full_name", "status", "place", "time_s", "time_text"], columns);
        Assert.Equal(expected.Count, rows.Count);
        foreach (var row in rows)
        {
            var r = expected[row[0].GetString()!];
            Assert.Equal(McpJson.StatusText(r.Status), row[1].GetString());
            Assert.Equal(r.Place, row[2].ValueKind == JsonValueKind.Null ? null : row[2].GetInt32());
            if (r.ResultTime is { } t)
                Assert.Equal(t.TotalSeconds, row[3].GetDouble(), 3);
            if (r.ResultTime is not null)
                Assert.Equal(ShownTime.Result(r.RankingTime, r.TimeDecimals), row[4].GetString());
        }
        Assert.Equal("MP", rows.Single(r => r[0].GetString() == "Шевченко Петро")[1].GetString());
    }

    [Fact]
    public async Task LegsCompareRunnersOfTheSameGroupOnTheSameControlPair()
    {
        await McpKit.CompetitionAsync("mcplegs");
        var (_, all) = await McpKit.QueryAsync("SELECT group_name, from_code, to_code, on_course, leg_s, leg_place FROM legs");
        Assert.True(all.Count > 0, "legs is empty");
        var (_, rows) = await McpKit.QueryAsync(
            "SELECT from_code, to_code, COUNT(*) AS n, MIN(leg_place) AS best, median(leg_s) AS med FROM legs " +
            "WHERE group_name = 'Ч21' AND on_course = 1 GROUP BY from_code, to_code ORDER BY from_code, to_code");
        Assert.True(rows.Any(r => r[0].GetString() == "31" && r[1].GetString() == "32" && r[2].GetInt32() == 2 && r[3].GetInt32() == 1),
            "rows: " + string.Join(" | ", rows.Select(r => string.Join(",", r.Select(v => v.ToString())))) +
            " ;; all: " + string.Join(" | ", all.Select(r => string.Join(",", r.Select(v => v.ToString())))));
        Assert.All(rows, r => Assert.True(r[4].GetDouble() > 0));
    }

    [Fact]
    public async Task DescribeDataListsTheTablesAndTheAdditionalFields()
    {
        var c = await McpKit.CompetitionAsync("mcpdesc");
        await McpKit.Editor.AddCustomFieldAsync("Стать", CustomFieldKind.Choice, [new ChoiceOption("Ч"), new ChoiceOption("Ж")]);
        var answer = await McpKit.CallAsync("describe_data", new { table = "participants" });
        var columns = answer.GetProperty("tables")[0].GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("name").GetString()).ToList();
        Assert.Contains("full_name", columns);
        Assert.Contains(columns, n => n!.StartsWith("field_", StringComparison.Ordinal));
        Assert.Contains("«Бали»", answer.GetProperty("rules").GetString());
    }

    [Fact]
    public async Task FindToleratesTheWrongLayoutAndGetContextNamesTheCompetition()
    {
        var c = await McpKit.CompetitionAsync("mcpfind");
        var context = await McpKit.CallAsync("get_context");
        Assert.Equal(c.Summary.Identifier, context.GetProperty("competition").GetProperty("identifier").GetString());
        var found = await McpKit.CallAsync("find", new { text = "Rjdfkm", kinds = new[] { "participant" } }); // «Коваль» typed in Latin layout
        Assert.Equal("Коваль Олег", found.GetProperty("participants")[0].GetProperty("full_name").GetString());
        var card = await McpKit.CallAsync("get_participant", new { participant = "3" });
        Assert.Equal("MP", card.GetProperty("days")[0].GetProperty("result").GetProperty("status").GetString());
    }
}

[Collection(AppCollection.Name)]
public sealed class McpSandboxTests
{
    private static readonly AnalyticsTable Numbers = new("numbers",
        [new AnalyticsColumn("n", AnalyticsType.Integer, ""), new AnalyticsColumn("t", AnalyticsType.Text, "")],
        Enumerable.Range(1, 1200).Select(i => new object?[] { (long)i, "x" + i }).ToList());

    private static Task<AnalyticsQueryResult> Run(AnalyticsSandbox sandbox, string sql, string key = "test|1|1") =>
        sandbox.QueryAsync(new AnalyticsQuery(key, sql, ["numbers"], (_, _) => Task.FromResult(Numbers), Timeout: TimeSpan.FromSeconds(1)));

    [Fact]
    public async Task OnlyReadingSelectsRun()
    {
        using var sandbox = new AnalyticsSandbox();
        Assert.Equal(1200L, (await Run(sandbox, "SELECT count(*) FROM numbers")).Rows[0][0]);
        foreach (var sql in new[]
                 {
                     "INSERT INTO numbers VALUES (1, 'a')", "UPDATE numbers SET t = 'y'", "DELETE FROM numbers",
                     "ATTACH DATABASE 'x.db' AS x", "PRAGMA query_only = OFF", "CREATE TABLE z (a)",
                     "SELECT 1; DELETE FROM numbers", "SELECT load_extension('x')",
                 })
        {
            var refused = await Record.ExceptionAsync(() => Run(sandbox, sql));
            Assert.True(refused is AnalyticsQueryException, $"{sql}: {refused?.GetType().Name ?? "ran"}");
        }
        Assert.Equal(1200L, (await Run(sandbox, "SELECT count(*) FROM numbers")).Rows[0][0]);
    }

    [Fact]
    public async Task ARunawayQueryIsStoppedAndBigResultsAreCut()
    {
        using var sandbox = new AnalyticsSandbox();
        var ex = await Assert.ThrowsAsync<AnalyticsQueryException>(() =>
            Run(sandbox, "WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM c) SELECT count(*) FROM c"));
        Assert.Contains("stopped", ex.Message);
        var big = await Run(sandbox, "SELECT * FROM numbers");
        Assert.True(big.Truncated);
        Assert.Equal(500, big.Rows.Count);
        Assert.Equal(1200, big.TotalRows);
    }

    [Fact]
    public async Task MedianAndPercentileAggregatePerGroup()
    {
        using var sandbox = new AnalyticsSandbox();
        var r = await Run(sandbox, "SELECT n % 2 AS odd, median(n), percentile(n, 90) FROM numbers WHERE n <= 10 GROUP BY odd ORDER BY odd");
        Assert.Equal(6.0, (double)r.Rows[0][1]!, 6);   // 2 4 6 8 10
        Assert.Equal(5.0, (double)r.Rows[1][1]!, 6);   // 1 3 5 7 9
        Assert.Equal(9.2, (double)r.Rows[0][2]!, 6);
    }
}

[Collection(AppCollection.Name)]
public sealed class McpWriteTests
{
    [Fact]
    public async Task AChangeIsOneUndoStepWithTheAssistantsOrigin()
    {
        var c = await McpKit.CompetitionAsync("mcpwrite");
        McpKit.Runtime.SetMode(McpMode.AutoChanges);
        var bus = McpKit.Sp.GetRequiredService<IDataChangeBus>();
        var origins = new List<string>();
        void OnChanged(object? s, DataChange change) => origins.Add(change.Origin);
        bus.Changed += OnChanged;
        try
        {
            var answer = await McpKit.CallAsync("update_participants", new
            {
                competition = c.Summary.Identifier,
                changes = new object[]
                {
                    new { participant = "Коваль Олег", day = 1, fields = new { group = "Ж21", note = "перевести", chip = "6001" } },
                    new { participant = "2", fields = new { coach = "Петренко" } },
                },
            });
            Assert.True(answer.GetProperty("done").GetBoolean());
            var step = Guid.Parse(answer.GetProperty("step_id").GetString()!);
            var link = await c.GetLinkAsync("Коваль Олег", 1);
            Assert.Equal(c.Groups["Ж21"].Id, link.GroupId);
            Assert.Equal("6001", link.Chip);
            Assert.True(origins.All(o => o.EndsWith(McpOrigin.Suffix, StringComparison.Ordinal)), string.Join(" | ", origins));
            Assert.NotEmpty(origins);
            Assert.Single(McpKit.Ui.Records);

            var undone = await McpKit.CallAsync("undo", new { step_id = step });
            Assert.True(undone.GetProperty("result").GetProperty("undone").GetBoolean());
            link = await c.GetLinkAsync("Коваль Олег", 1);
            Assert.Equal(c.Groups["Ч21"].Id, link.GroupId);
            Assert.Equal("5001", link.Chip);
            var roster = await McpKit.Editor.GetParticipantRosterAsync();
            Assert.Equal("", roster.Single(p => p.Number == "2").Coach);
        }
        finally
        {
            bus.Changed -= OnChanged;
            McpKit.Runtime.SetMode(McpMode.ReadOnly);
        }
    }

    [Fact]
    public async Task AChangeOfAnotherCompetitionOrATakenBibIsRefused()
    {
        var c = await McpKit.CompetitionAsync("mcpref");
        McpKit.Runtime.SetMode(McpMode.AutoChanges);
        try
        {
            var other = await McpKit.ErrorAsync("update_participants", new
            {
                competition = "not-this-one",
                changes = new[] { new { participant = "1", fields = new { note = "x" } } },
            });
            Assert.Contains("Nothing was changed", other);
            var bib = await McpKit.ErrorAsync("update_participants", new
            {
                competition = c.Summary.Identifier,
                changes = new[] { new { participant = "1", fields = new { bib = "2" } } },
            });
            Assert.Contains("Бондар Іван", bib);
            var chip = await McpKit.ErrorAsync("update_participants", new
            {
                competition = c.Summary.Identifier,
                changes = new[] { new { participant = "1", day = 1, fields = new { chip = "5002" } } },
            });
            Assert.Contains("on_chip_conflict", chip);
            Assert.Equal("1", (await McpKit.Editor.GetParticipantRosterAsync()).Single(p => p.FullName == "Коваль Олег").Number);
        }
        finally
        {
            McpKit.Runtime.SetMode(McpMode.ReadOnly);
        }
    }

    [Fact]
    public async Task AnUnansweredConfirmationIsPendingAndRunsWhenAllowed()
    {
        var c = await McpKit.CompetitionAsync("mcpconf");
        McpKit.Runtime.SetMode(McpMode.ConfirmChanges);
        var confirmations = McpKit.Sp.GetRequiredService<McpConfirmations>();
        var wait = confirmations.Wait;
        var release = new TaskCompletionSource<McpDecision>();
        McpKit.Ui.Decide = _ => release.Task;
        confirmations.Wait = TimeSpan.FromMilliseconds(300);
        try
        {
            var pending = await McpKit.CallAsync("update_participants", new
            {
                competition = c.Summary.Identifier,
                changes = new[] { new { participant = "11", fields = new { note = "чекає" } } },
            });
            Assert.Equal("pending", pending.GetProperty("status").GetString());
            var id = pending.GetProperty("confirmation_id").GetString();
            Assert.Single(McpKit.Ui.Requests);
            Assert.Equal("", (await McpKit.Editor.GetParticipantRosterAsync()).Single(p => p.Number == "11").Note);

            release.SetResult(new McpDecision(true));
            confirmations.Wait = TimeSpan.FromSeconds(10);
            var done = await McpKit.CallAsync("await_confirmation", new { confirmation_id = id });
            Assert.True(done.GetProperty("done").GetBoolean());
            Assert.Equal("чекає", (await McpKit.Editor.GetParticipantRosterAsync()).Single(p => p.Number == "11").Note);

            McpKit.Ui.Decide = _ => Task.FromResult(McpDecision.Rejected);
            var refused = await McpKit.ErrorAsync("update_participants", new
            {
                competition = c.Summary.Identifier,
                changes = new[] { new { participant = "11", fields = new { note = "ні" } } },
            });
            Assert.Contains("did not allow", refused);
        }
        finally
        {
            confirmations.Wait = wait;
            McpKit.Ui.Reset();
            McpKit.Runtime.SetMode(McpMode.ReadOnly);
        }
    }

    [Fact]
    public async Task ADrawIsAPreviewFirstAndThePlanAppliesWhatItShowed()
    {
        var c = await CompetitionBuilder.CreateAsync(McpKit.Sp, TestIds.Next("mcpdraw"));
        await c.AddControlsAsync(1, "31", "32");
        await c.AddGroupAsync("Ч21", "31 32");
        await c.AddGroupAsync("Ж21", "32 31");
        for (var i = 1; i <= 6; i++)
            await c.AddRunnerAsync($"Учасник {i}", i.ToString(), (7000 + i).ToString(), i <= 3 ? "Ч21" : "Ж21");
        await c.SelectAsync(1);
        McpKit.Ui.Reset();
        McpKit.Runtime.SetMode(McpMode.AutoChanges);
        try
        {
            var preview = await McpKit.CallAsync("draw_start_times", new
            {
                competition = c.Summary.Identifier,
                settings = new { global_start = "11:00:00", interval = "00:02:00" },
            });
            Assert.Equal(6, preview.GetProperty("preview").GetProperty("starts").GetInt32());
            Assert.All(await Task.WhenAll(c.Runners.Keys.Select(n => c.GetLinkAsync(n, 1))), l => Assert.Null(l.StartTime));
            var shown = preview.GetProperty("preview").GetProperty("start_list").EnumerateArray()
                .ToDictionary(r => r.GetProperty("name").GetString()!, r => r.GetProperty("start").GetString());

            await McpKit.CallAsync("apply_plan", new { plan_id = preview.GetProperty("plan_id").GetString() });
            Assert.Single(McpKit.Ui.Requests);  // risky: confirmed even without the confirmation mode
            foreach (var (name, start) in shown)
                Assert.Equal(start, McpTime.Clock((await c.GetLinkAsync(name, 1)).StartTime!.Value));
            Assert.Contains("Unknown or expired", await McpKit.ErrorAsync("apply_plan", new { plan_id = preview.GetProperty("plan_id").GetString() }));
        }
        finally
        {
            McpKit.Runtime.SetMode(McpMode.ReadOnly);
        }
    }
}

[Collection(AppCollection.Name)]
public sealed class McpServerTests
{
    private static McpHttpServer NewServer(string endpointPath) => new(
        McpKit.Sp.GetRequiredService<IAppStore>(),
        McpKit.Dispatcher,
        McpKit.Runtime,
        McpKit.Sp.GetRequiredService<McpConfirmations>(),
        McpKit.Sp.GetRequiredService<ISessionService>(),
        McpKit.Sp,
        null,
        new McpHostOptions { PortOverride = 0, EndpointPath = endpointPath, TakeoverEvery = TimeSpan.FromMilliseconds(200) });

    private static async Task<McpSettings> EnableAsync(McpMode mode)
    {
        var store = McpKit.Sp.GetRequiredService<IAppStore>();
        var before = await store.GetMcpSettingsAsync();
        await store.SaveMcpSettingsAsync(new McpSettings { Enabled = true, Mode = mode, Token = "test-token-" + Guid.NewGuid().ToString("N") });
        return before;
    }

    [Fact]
    public async Task TheServerServesToolsOnlyWithTheTokenAndFromThisComputer()
    {
        await McpKit.CompetitionAsync("mcphttp");
        var endpoint = Path.Combine(TestEnvironment.Instance.DataRoot, $"mcp-{Guid.NewGuid():N}.json");
        var store = McpKit.Sp.GetRequiredService<IAppStore>();
        var before = await EnableAsync(McpMode.ReadOnly);
        await using var server = NewServer(endpoint);
        try
        {
            await server.ApplySettingsAsync();
            Assert.Equal(McpServerState.Running, server.State);
            var file = McpEndpointFile.Read(endpoint);
            Assert.NotNull(file);
            var token = (await store.GetMcpSettingsAsync()).Token;
            Assert.Equal(token, file!.Token);

            await using (var client = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
                         {
                             Endpoint = new Uri(server.Url!),
                             AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token },
                         })))
            {
                var tools = await client.ListToolsAsync();
                Assert.Equal(McpToolCatalog.All.Count(t => t.Class == McpToolClass.Read), tools.Count);
                Assert.Contains(tools, t => t.Name == "query");
                var context = await client.CallToolAsync("get_context");
                Assert.NotEqual(true, context.IsError);
                Assert.Equal(4, (await client.ListPromptsAsync()).Count);
            }

            using var http = new HttpClient();
            var initialize = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}""";
            HttpRequestMessage Request(string? bearer, string? origin)
            {
                var r = new HttpRequestMessage(HttpMethod.Post, server.Url) { Content = new StringContent(initialize, Encoding.UTF8, "application/json") };
                r.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                r.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                if (bearer is not null)
                    r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
                if (origin is not null)
                    r.Headers.Add("Origin", origin);
                return r;
            }
            Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Request(null, null))).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Request("wrong", null))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(Request(token, "https://evil.example"))).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(Request(token, "http://localhost:3000"))).StatusCode);

            // Switched off: the discovery file goes away, nothing listens.
            await store.SaveMcpSettingsAsync(new McpSettings { Enabled = false, Mode = McpMode.ReadOnly, Token = token, Port = 0 });
            await server.ApplySettingsAsync();
            Assert.Equal(McpServerState.Off, server.State);
            Assert.Null(McpEndpointFile.Read(endpoint));
        }
        finally
        {
            await store.SaveMcpSettingsAsync(before);
            McpKit.Runtime.SetMode(McpMode.ReadOnly);
        }
    }

    [Fact]
    public async Task TheBridgeForwardsOverStdioAndAnswersWhileNoWindowServes()
    {
        await McpKit.CompetitionAsync("mcpbridge");
        var endpoint = Path.Combine(TestEnvironment.Instance.DataRoot, $"mcp-{Guid.NewGuid():N}.json");
        var store = McpKit.Sp.GetRequiredService<IAppStore>();
        var before = await EnableAsync(McpMode.ReadOnly);
        await using var server = NewServer(endpoint);
        var toBridge = new Pipe();
        var fromBridge = new Pipe();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var bridge = Task.Run(() => McpBridge.RunAsync(toBridge.Reader.AsStream(), fromBridge.Writer.AsStream(), [endpoint], stop.Token));
        var writer = new StreamWriter(toBridge.Writer.AsStream(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        var reader = new StreamReader(fromBridge.Reader.AsStream(), Encoding.UTF8);
        async Task<JsonElement> Ask(int id, string method, string @params)
        {
            await writer.WriteLineAsync($$"""{"jsonrpc":"2.0","id":{{id}},"method":"{{method}}","params":{{@params}}}""");
            while (true)
            {
                var line = await reader.ReadLineAsync(stop.Token) ?? throw new InvalidOperationException("The bridge closed.");
                var message = JsonDocument.Parse(line).RootElement;
                if (message.TryGetProperty("id", out var got) && got.GetInt32() == id)
                    return message.Clone();
            }
        }

        try
        {
            var init = await Ask(1, "initialize", """{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"claude-desktop-test","version":"1"}}""");
            Assert.Equal("orientpyx", init.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());
            await writer.WriteLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");

            // No window serves yet: the tools are listed anyway, a call says why it cannot run.
            var offline = await Ask(2, "tools/list", "{}");
            Assert.Equal(McpToolCatalog.All.Count(t => t.Class == McpToolClass.Read), offline.GetProperty("result").GetProperty("tools").GetArrayLength());
            var refused = await Ask(3, "tools/call", """{"name":"get_context","arguments":{}}""");
            Assert.True(refused.GetProperty("result").GetProperty("isError").GetBoolean());

            await server.ApplySettingsAsync();
            Assert.Equal(McpServerState.Running, server.State);
            var context = await Ask(4, "tools/call", """{"name":"get_context","arguments":{}}""");
            Assert.False(context.GetProperty("result").TryGetProperty("isError", out var isError) && isError.GetBoolean(), context.ToString());
            var text = context.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
            Assert.Contains("\"mode\":\"read_only\"", text);
        }
        finally
        {
            stop.Cancel();
            await store.SaveMcpSettingsAsync(before);
            await server.StopAsync();
            try { await bridge; } catch (OperationCanceledException) { }
        }
    }
}
