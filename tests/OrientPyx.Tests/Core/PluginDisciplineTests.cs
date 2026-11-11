using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.Tests.Support;

namespace OrientPyx.Tests.Core;

/// <summary>The two standard plugins judged through the real pipeline: «Штраф замість зняття» and «Зважена естафета».</summary>
[Collection(AppCollection.Name)]
public sealed class PluginDisciplineTests
{
    private static IServiceProvider Sp => TestEnvironment.Instance.Services;

    private static TimeSpan T(int h, int m, int s = 0) => new(h, m, s);

    private static async Task<IDisposable> PluginsOnAsync(params string[] ids)
    {
        var catalog = Sp.GetRequiredService<IPluginCatalog>();
        var before = ids.ToDictionary(id => id, catalog.IsEnabled);
        foreach (var id in ids)
            await catalog.SetEnabledAsync(id, true);
        return new Restore(async () =>
        {
            foreach (var (id, enabled) in before)
                await catalog.SetEnabledAsync(id, enabled);
        });
    }

    private sealed class Restore(Func<Task> restore) : IDisposable
    {
        public void Dispose() => restore().GetAwaiter().GetResult();
    }

    [Fact]
    public async Task MissedControlPenalty_TurnsAnMpIntoOkWithATimePenalty()
    {
        using var _ = await PluginsOnAsync("missed-penalty");
        var id = new DisciplineId("missed-penalty.set-course");
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("mcp"), discipline: id);
        await c.AddControlsAsync(1, "31", "32", "33", "34");
        await c.AddGroupAsync("Ч21", "31 32 33 34");
        await c.AddRunnerAsync("Clean", "1", "1001", "Ч21", T(10, 0));
        await c.AddRunnerAsync("OneMissed", "2", "1002", "Ч21", T(10, 0));
        await c.AddRunnerAsync("TooMany", "3", "1003", "Ч21", T(10, 0));
        await c.AddReadoutAsync(1, "1001", "31 32 33 34", T(10, 0), T(10, 33));
        await c.AddReadoutAsync(1, "1002", "31 33 34", T(10, 0), T(10, 30));
        await c.AddReadoutAsync(1, "1003", "", T(10, 0), T(10, 20));           // all four missed > maxMissed 3

        var r = await c.ResultsAsync();
        Assert.Equal(FinishStatus.Ok, r["OneMissed"].Status);
        Assert.Equal(["32"], r["OneMissed"].Missed);
        Assert.Equal(T(0, 30), r["OneMissed"].ResultTime);
        Assert.Equal(T(0, 35), r["OneMissed"].AdjustedTime);            // 5 default minutes × 1 missed
        Assert.Equal(1, Convert.ToInt32(r["OneMissed"].Extra["missed"]));
        Assert.Equal(FinishStatus.Mp, r["TooMany"].Status);
        Assert.Equal(4, r["TooMany"].Missed.Count);
        Assert.Equal(1, r["Clean"].Place);                               // 33:00 beats the adjusted 35:00
        Assert.Equal(2, r["OneMissed"].Place);
        Assert.Null(r["Clean"].AwardedRank);
        var data = await c.ProtocolDataAsync();
        Assert.Contains(data.ExtraColumns, col => col.Key == "missed-penalty.missed");
    }

    [Fact]
    public async Task MissedControlPenalty_DaySettingsChangeTheMinutesAndTheLimit()
    {
        using var _ = await PluginsOnAsync("missed-penalty");
        var id = new DisciplineId("missed-penalty.set-course");
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("mcp2"), discipline: id);
        await c.AddControlsAsync(1, "31", "32", "33", "34");
        await c.AddGroupAsync("Ч21", "31 32 33 34");
        await c.AddRunnerAsync("One", "1", "1001", "Ч21", T(10, 0));
        await c.AddRunnerAsync("Two", "2", "1002", "Ч21", T(10, 0));
        await c.AddReadoutAsync(1, "1001", "31 33 34", T(10, 0), T(10, 30));
        await c.AddReadoutAsync(1, "1002", "31 34", T(10, 0), T(10, 30));
        await c.SelectAsync();
        var editor = Sp.GetRequiredService<ICompetitionEditorService>();
        await editor.SaveDisciplineSettingsAsync(c.Day(1).Id, id, new Dictionary<string, object?> { ["minutes"] = 2, ["maxMissed"] = 1 });

        var r = await c.ResultsAsync();
        Assert.Equal(T(0, 32), r["One"].AdjustedTime);
        Assert.Equal(FinishStatus.Mp, r["Two"].Status);                  // 2 missed > maxMissed 1

        var settings = await editor.GetDisciplineSettingsAsync(c.Day(1).Id, id);
        Assert.NotNull(settings);
    }

    [Fact]
    public async Task WeightedRelay_MultipliesLegTimesByTheAgeGenderCoefficient()
    {
        using var _ = await PluginsOnAsync("weighted");
        var id = new DisciplineId("weighted.relay");
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("wr"), discipline: id, date: new DateTimeOffset(2026, 6, 6, 0, 0, 0, CompetitionBuilder.Offset));
        await c.AddControlsAsync(1, "31");
        await c.AddGroupAsync("W", "31", s => { s.LegCount = 2; s.MassStartTime = T(10, 0); });
        var man = await c.AddRunnerAsync("Man", "", "5011", "W", configure: p => p.BirthDate = new DateTimeOffset(1980, 5, 1, 0, 0, 0, TimeSpan.Zero));
        var woman = await c.AddRunnerAsync("Woman", "", "5012", "W", configure: p => p.BirthDate = new DateTimeOffset(1996, 5, 1, 0, 0, 0, TimeSpan.Zero));
        await c.AddRunnerAsync("NoGender-1", "", "5021", "W", configure: p => p.BirthDate = new DateTimeOffset(1990, 5, 1, 0, 0, 0, TimeSpan.Zero));
        await c.AddRunnerAsync("NoGender-2", "", "5022", "W", configure: p => p.BirthDate = new DateTimeOffset(1990, 5, 1, 0, 0, 0, TimeSpan.Zero));
        await c.AddRelayTeamAsync("W", 501, "Mixed", 1, ("Man", 1), ("Woman", 2));
        await c.AddRelayTeamAsync("W", 502, "Unknown", 1, ("NoGender-1", 1), ("NoGender-2", 2));
        await c.AddReadoutAsync(1, "5011", "31", null, T(10, 20));
        await c.AddReadoutAsync(1, "5012", "31", null, T(10, 45));
        await c.AddReadoutAsync(1, "5021", "31", null, T(10, 10));
        await c.AddReadoutAsync(1, "5022", "31", null, T(10, 20));

        await c.SelectAsync();
        var editor = Sp.GetRequiredService<ICompetitionEditorService>();
        await editor.EnsurePluginFieldsAsync();
        Assert.Equal("Ч", await editor.SetParticipantFieldAsync(man.Id, "gender", "ч"));   // case folded to the option
        Assert.Equal("Ж", await editor.SetParticipantFieldAsync(woman.Id, "gender", "Ж"));
        Assert.Equal("M", await editor.SetParticipantFieldAsync(woman.Id, "gender", "M")); // no synonyms: kept as typed
        Assert.Equal("Ж", await editor.SetParticipantFieldAsync(woman.Id, "gender", "ж"));

        var r = await c.ResultsAsync();
        Assert.Equal(T(0, 20), r["Man"].ResultTime);
        Assert.Equal(T(0, 18, 12), r["Man"].AdjustedTime);     // age 46 → K 0.91
        Assert.Equal(T(0, 22, 15), r["Woman"].AdjustedTime);   // age 30 → K 0.89
        Assert.Equal(T(0, 20), r["Man"].Extra["time"]);         // «Час» — the clean leg time T
        Assert.Equal(T(0, 25), r["Woman"].Extra["time"]);
        Assert.Equal(T(0, 10), r["NoGender-1"].Extra["time"]);  // shown even when K is unknown
        Assert.Equal(T(0, 40, 27), r["Man"].Team!.Time);
        Assert.Equal(1, r["Man"].Team!.Place);
        Assert.Equal("weighted.k.missing", r["NoGender-1"].UnrankedReason);
        Assert.Null(r["NoGender-1"].Team!.Place);
        var data = await c.ProtocolDataAsync();
        Assert.Equal(["weighted.time", "weighted.k", "weighted.adj"],
            data.ExtraColumns.Where(col => col.Plugin is not null).Select(col => col.Key));

        // The monitor's leg rows: «Результат» is the counted T·K (as in the protocol), «Час» the clean T, the running
        // team time in «Наростаючий» — «Відставання» is the team's alone.
        var monitor = await editor.BuildMonitorPreviewAsync(c.Day(1).Id, Labels.Make<BusinessLogic.Models.MonitorFile>(),
            new BusinessLogic.Models.ResultColumnSelection(["full_name", "weighted.time", "result_time", "gap", "cumulative"]),
            Labels.Make<BusinessLogic.Models.MonitorLabels>());
        var monitorRows = monitor!.Groups.Single().Cells;
        Assert.Equal(["Man", "20:00", "18:12", "", "18:12"], monitorRows.Single(row => row.Values[0] == "Man").Values);
        Assert.Equal(["Woman", "25:00", "22:15", "", "40:27"], monitorRows.Single(row => row.Values[0] == "Woman").Values);

        // Online: the leg carries both times and the extra column values (plugin columns and the «Стать» field).
        var online = await editor.GetOnlineResultsSnapshotAsync(c.Day(1).Id);
        var onlineLeg = online.Teams.Single(t => t.Number == 501).Legs[0];
        Assert.Equal(T(0, 20), onlineLeg.LegTime);           // «Час»
        Assert.Equal(T(0, 18, 12), onlineLeg.Result);        // «Результат»
        Assert.Equal("Ч", onlineLeg.Extra["field.gender"]);
        Assert.Equal("18:12", onlineLeg.Extra["weighted.adj"]);
        Assert.Contains(online.ExtraColumns, x => x.Key == "field.gender" && !x.IsPlugin);
        Assert.Contains(online.ExtraColumns, x => x.Key == "weighted.time" && x.IsPlugin);
        var fields = await editor.GetCustomFieldsAsync();
        Assert.Contains(fields, f => f.Key == "gender" && f.Source == "plugin:weighted");
    }

    [Fact]
    public async Task PluginColumns_StayOutOfAnotherDisciplinesGroup()
    {
        using var _ = await PluginsOnAsync("weighted");
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("wrcols"), discipline: new DisciplineId("weighted.relay"),
            date: new DateTimeOffset(2026, 6, 6, 0, 0, 0, CompetitionBuilder.Offset));
        await c.AddControlsAsync(1, "31");
        await c.AddGroupAsync("W", "31", s => { s.LegCount = 1; s.MassStartTime = T(10, 0); });
        await c.AddGroupAsync("S", "31", s => s.DisciplineOverride = DisciplineId.SetCourse);   // «Заданий напрямок»
        await c.AddRunnerAsync("Man", "", "5011", "W", configure: p => p.BirthDate = new DateTimeOffset(1980, 5, 1, 0, 0, 0, TimeSpan.Zero));
        await c.AddRelayTeamAsync("W", 501, "Mixed", 1, ("Man", 1));
        await c.AddRunnerAsync("Solo", "7", "5031", "S", T(10, 0));
        await c.AddReadoutAsync(1, "5011", "31", null, T(10, 20));
        await c.AddReadoutAsync(1, "5031", "31", T(10, 0), T(10, 30));
        await c.SelectAsync();
        var editor = Sp.GetRequiredService<ICompetitionEditorService>();
        await editor.EnsurePluginFieldsAsync();
        await editor.SetParticipantFieldAsync(c.Runners["Man"].Id, "gender", "Ч");

        // The protocol: «Час», «K», «T·K» in the relay's table only; the «Стать» field in both.
        var data = await c.ProtocolDataAsync();
        var extraKeys = new[] { "weighted.time", "weighted.k", "weighted.adj", "field.gender" };
        var settings = new BusinessLogic.Models.ResultProtocolSettings();
        foreach (var key in extraKeys)
            settings.Columns.Add(new BusinessLogic.Models.ProtocolColumnSetting
                { Column = BusinessLogic.Models.ProtocolColumn.Extra, Key = key, Visible = true });
        var doc = Sp.GetRequiredService<IResultProtocolBuilder>().Build(data, settings, Labels.Make<ProtocolLabels>());
        var pluginLabels = data.ExtraColumns.Where(x => x.Plugin is not null).Select(x => x.LabelKey).ToHashSet();
        var pluginIndices = doc.ColumnHeaders.Select((h, i) => (h, i)).Where(x => pluginLabels.Contains(x.h)).Select(x => x.i).ToList();
        Assert.Equal(3, pluginIndices.Count);
        Assert.Empty(doc.Sections.Single(s => s.GroupName == "W").HiddenColumns);
        Assert.Equal(pluginIndices, doc.Sections.Single(s => s.GroupName == "S").HiddenColumns);

        // …and the Word file: the set-course group's table has three columns fewer.
        var bytes = Sp.GetRequiredService<IResultProtocolWriter>().Write(doc);
        using (var word = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(new MemoryStream(bytes), false))
        {
            var widths = word.MainDocumentPart!.Document.Body!.Elements<DocumentFormat.OpenXml.Wordprocessing.Table>()
                .Select(t => t.Elements<DocumentFormat.OpenXml.Wordprocessing.TableRow>().First()
                    .Elements<DocumentFormat.OpenXml.Wordprocessing.TableCell>().Count())
                .OrderBy(n => n)
                .ToList();
            Assert.Equal([doc.ColumnHeaders.Count - 3, doc.ColumnHeaders.Count], widths);
        }

        // The monitor: the same, on the screen and in the HTML file.
        var monitor = await editor.BuildMonitorPreviewAsync(c.Day(1).Id, Labels.Make<BusinessLogic.Models.MonitorFile>(),
            new BusinessLogic.Models.ResultColumnSelection(["full_name", "weighted.time", "weighted.k", "result_time", "field.gender"]),
            Labels.Make<BusinessLogic.Models.MonitorLabels>());
        Assert.Empty(monitor!.Groups.Single(g => g.Name == "W").HiddenColumns);
        Assert.Equal([1, 2], monitor.Groups.Single(g => g.Name == "S").HiddenColumns);
        var html = System.Text.Encoding.UTF8.GetString(Sp.GetRequiredService<IMonitorHtmlWriter>().Write(monitor));
        int Headers(string group)
        {
            var at = html.IndexOf($"<h2>{group}</h2>", StringComparison.Ordinal);
            var end = html.IndexOf("</thead>", at, StringComparison.Ordinal);
            return System.Text.RegularExpressions.Regex.Matches(html[at..end], "<th ").Count;
        }
        Assert.Equal(5, Headers("W"));
        Assert.Equal(3, Headers("S"));
    }
}
