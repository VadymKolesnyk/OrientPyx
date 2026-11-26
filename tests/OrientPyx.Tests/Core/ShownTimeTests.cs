using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Tests.Support;

namespace OrientPyx.Tests.Core;

/// <summary>
/// How a result time is shown (<see cref="ShownTime"/>): fractions dropped in the text only, never rounded, while every
/// computed time (a changed one, a team or a summary total) keeps its full precision (Р-6, Р-49); a tie of seconds
/// between two places shows the tenths that tell them apart; «Відставання» is the difference of the shown times.
/// </summary>
[Collection(AppCollection.Name)]
public sealed class ShownTimeTests
{
    private static IServiceProvider Sp => TestEnvironment.Instance.Services;

    private static TimeSpan T(int h, int m, int s = 0, int ms = 0) => new(0, h, m, s, ms);

    [Fact]
    public void Truncate_DropsTheFractionNeverRoundsIt()
    {
        var t = T(0, 17, 56, 664);
        Assert.Equal(T(0, 17, 56), ShownTime.Truncate(t));
        Assert.Equal(T(0, 17, 56, 600), ShownTime.Truncate(t, 1));
        Assert.Equal(T(0, 17, 56, 660), ShownTime.Truncate(t, 2));
        Assert.Equal(t, ShownTime.Truncate(t, 3));
    }

    [Fact]
    public void Format_AddsOnlyTheAskedDigits()
    {
        var t = T(0, 17, 56, 664);
        Assert.Equal("0:17:56", ShownTime.Result(t));
        Assert.Equal("0:17:56.6", ShownTime.Result(t, 1));
        Assert.Equal("0:17:56.664", ShownTime.Result(t, 3));
        Assert.Equal("17:56.6", ShownTime.Span(t, 1));
        Assert.Equal("1:02:03.05", ShownTime.Result(T(1, 2, 3, 50), 2));
        Assert.Equal("0:17:56.0", ShownTime.Result(T(0, 17, 56), 1));
        Assert.Equal(string.Empty, ShownTime.Result(null));
        Assert.Equal(string.Empty, ShownTime.Result(TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void Gap_IsTheDifferenceOfTheShownTimes()
    {
        // 17:57.3 − 17:56.4 = 0.9 s, but the times show as 17:57 and 17:56 — the gap shows +1 s.
        Assert.Equal("+0:00:01", ShownTime.ResultGap(T(0, 17, 57, 300), T(0, 17, 56, 400)));
        Assert.Equal("+0:00:00.2", ShownTime.ResultGap(T(0, 17, 56, 600), T(0, 17, 56, 400), 1));
        Assert.Equal("+0:00.2", ShownTime.SpanGap(T(0, 17, 56, 600), T(0, 17, 56, 400), 1));
        Assert.Equal(string.Empty, ShownTime.ResultGap(T(0, 17, 56, 600), T(0, 17, 56, 400)));   // same shown second
    }

    [Fact]
    public void Decimals_OnlyForASecondSharedByTwoPlaces()
    {
        var items = new (string Id, TimeSpan? Time, int? Place, int? Score)[]
        {
            ("a", T(0, 17, 56, 400), 1, null),
            ("b", T(0, 17, 56, 600), 2, null),
            ("guest", T(0, 17, 56, 500), null, null),      // unplaced: takes the cluster's digits, never causes them
            ("c", T(0, 17, 57, 300), 3, null),
            ("d1", T(0, 18, 10, 410), 4, null),
            ("d2", T(0, 18, 10, 450), 5, null),             // tenths equal → hundredths
            ("e1", T(0, 19, 0, 200), 6, null),
            ("e2", T(0, 19, 0, 200), 6, null),              // an exact tie shares the place: no digits
            ("t1", T(0, 19, 30, 0), 8, null),
            ("t2", T(0, 19, 30, 0), 9, null),              // equal times placed apart by a tie-break: digits can't help
            ("s1", T(0, 20, 0, 100), 10, 10),
            ("s2", T(0, 20, 0, 900), 11, 9),                 // same second, another score: told apart by the score
        };
        var d = ShownTime.Decimals(items, x => x.Id, x => x.Time, x => x.Place, x => x.Score);

        Assert.Equal(1, d["a"]);
        Assert.Equal(1, d["b"]);
        Assert.Equal(1, d["guest"]);
        Assert.False(d.ContainsKey("c"));
        Assert.Equal(2, d["d1"]);
        Assert.Equal(2, d["d2"]);
        Assert.False(d.ContainsKey("e1"));
        Assert.False(d.ContainsKey("t1"));
        Assert.False(d.ContainsKey("s1"));
        Assert.False(d.ContainsKey("s2"));
    }

    [Fact]
    public async Task SetCourse_TiedSecondsShowTheTenthsThroughEveryOutput()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("tenths"));
        await c.AddControlsAsync(1, "31");
        await c.AddGroupAsync("Ч21", "31");
        await c.AddRunnerAsync("A", "1", "2001", "Ч21", T(10, 0));
        await c.AddRunnerAsync("B", "2", "2002", "Ч21", T(10, 0));
        await c.AddRunnerAsync("C", "3", "2003", "Ч21", T(10, 0));
        await c.AddRunnerAsync("Guest", "4", "2004", "Ч21", T(10, 0), configureDay: x => x.OutOfCompetition = true);
        await c.AddReadoutAsync(1, "2001", "31", T(10, 0), T(10, 17, 56, 400));
        await c.AddReadoutAsync(1, "2002", "31", T(10, 0), T(10, 17, 56, 600));
        await c.AddReadoutAsync(1, "2003", "31", T(10, 0), T(10, 17, 57, 300));
        await c.AddReadoutAsync(1, "2004", "31", T(10, 0), T(10, 17, 56, 500));

        var r = await c.ResultsAsync();
        Assert.Equal(T(0, 17, 56, 400), r["A"].ResultTime);    // stored with full precision
        Assert.Equal(new int?[] { 1, 2, 3 }, new[] { r["A"].Place, r["B"].Place, r["C"].Place });
        Assert.Equal([1, 1, 0, 1], new[] { r["A"].TimeDecimals, r["B"].TimeDecimals, r["C"].TimeDecimals, r["Guest"].TimeDecimals });

        var data = await c.ProtocolDataAsync();
        var settings = new ResultProtocolSettings();
        foreach (var col in settings.Columns)
            col.Visible = col.Column is ProtocolColumn.FullName or ProtocolColumn.Result or ProtocolColumn.Place or ProtocolColumn.Gap;
        var labels = Labels.Make<ProtocolLabels>() with
        {
            ColumnHeaders = Enum.GetValues<ProtocolColumn>().ToDictionary(x => x, x => x.ToString()),
        };
        var doc = Sp.GetRequiredService<IResultProtocolBuilder>().Build(data, settings, labels);
        var headers = doc.ColumnHeaders.ToList();
        var (name, result, gap) = (headers.IndexOf("FullName"), headers.IndexOf("Result"), headers.IndexOf("Gap"));
        var rows = Assert.Single(doc.Sections).Rows.Select(x => x.Cells).ToDictionary(x => x[name]);
        Assert.Equal("0:17:56.4", rows["A"][result]);
        Assert.Equal("0:17:56.6", rows["B"][result]);
        Assert.Equal("+0:00:00.2", rows["B"][gap]);
        Assert.Equal("0:17:57", rows["C"][result]);
        Assert.Equal("+0:00:01", rows["C"][gap]);               // 17:57 − 17:56, as printed
        Assert.Equal("0:17:56.5", rows["Guest"][result]);

        // The finish log shows the same.
        await c.SelectAsync();
        var log = await Sp.GetRequiredService<ICompetitionEditorService>().GetFinishReadoutRowsAsync();
        Assert.Equal(1, log.Single(x => x.FullName == "B").TimeDecimals);
        Assert.Equal(T(0, 0, 0, 200), log.Single(x => x.FullName == "B").Gap);
        Assert.Equal(T(0, 0, 1), log.Single(x => x.FullName == "C").Gap);
    }

    [Fact]
    public async Task MissedControlPenalty_ChangedTimeKeepsItsPrecision()
    {
        var catalog = Sp.GetRequiredService<IPluginCatalog>();
        var wasOn = catalog.IsEnabled("missed-penalty");
        await catalog.SetEnabledAsync("missed-penalty", true);
        try
        {
            var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("mcpcut"), discipline: new DisciplineId("missed-penalty.set-course"));
            await c.AddControlsAsync(1, "31", "32");
            await c.AddGroupAsync("Ч21", "31 32");
            await c.AddRunnerAsync("One", "1", "3001", "Ч21", T(10, 0));
            await c.AddReadoutAsync(1, "3001", "31", T(10, 0), T(10, 17, 56, 664));

            var r = await c.ResultsAsync();
            Assert.Equal(T(0, 17, 56, 664), r["One"].ResultTime);
            Assert.Equal(T(0, 22, 56, 664), r["One"].AdjustedTime);   // computed with full precision…
            Assert.Equal("0:22:56", ShownTime.Result(r["One"].RankingTime));   // …shown as «Час» 17:56 + 5:00, never 22:57
        }
        finally
        {
            await catalog.SetEnabledAsync("missed-penalty", wasOn);
        }
    }

    [Fact]
    public async Task WeightedRelay_KOneGivesTheShownTime()
    {
        var catalog = Sp.GetRequiredService<IPluginCatalog>();
        var wasOn = catalog.IsEnabled("weighted");
        await catalog.SetEnabledAsync("weighted", true);
        try
        {
            var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("wrk1"), discipline: new DisciplineId("weighted.relay"),
                date: new DateTimeOffset(2026, 11, 25, 0, 0, 0, CompetitionBuilder.Offset));
            await c.AddControlsAsync(1, "31");
            await c.AddGroupAsync("Мікс", "31", s => { s.LegCount = 2; s.MassStartTime = T(18, 20); });
            var first = await c.AddRunnerAsync("First", "", "4001", "Мікс", configure: p => p.BirthDate = new DateTimeOffset(1997, 9, 13, 0, 0, 0, TimeSpan.Zero));
            var second = await c.AddRunnerAsync("Second", "", "4002", "Мікс", configure: p => p.BirthDate = new DateTimeOffset(1999, 9, 29, 0, 0, 0, TimeSpan.Zero));
            await c.AddRelayTeamAsync("Мікс", 11, "Team", 1, ("First", 1), ("Second", 2));
            await c.AddReadoutAsync(1, "4001", "31", null, T(18, 37, 56, 664));
            await c.AddReadoutAsync(1, "4002", "31", null, T(19, 0, 0, 500));
            await c.SelectAsync();
            var editor = Sp.GetRequiredService<ICompetitionEditorService>();
            await editor.EnsurePluginFieldsAsync();
            await editor.SetParticipantFieldAsync(first.Id, "gender", "Ч");
            await editor.SetParticipantFieldAsync(second.Id, "gender", "Ч");

            var r = await c.ResultsAsync();
            Assert.Equal(1.0, Convert.ToDouble(r["First"].Extra["k"]));         // age 29 → K 1.000
            Assert.Equal(T(0, 17, 56, 664), r["First"].Extra["time"]);          // «Час» and «T·K» keep the precision…
            Assert.Equal(T(0, 17, 56, 664), r["First"].Extra["adj"]);
            Assert.Equal(T(0, 17, 56, 664), r["First"].AdjustedTime);
            Assert.Equal(T(0, 22, 3, 836), r["Second"].AdjustedTime);          // 19:00:00.5 − 18:37:56.664
            Assert.Equal(T(0, 40, 0, 500), r["First"].Team!.Time);              // Σ of the precise legs

            // …and both show the same: 17:56, not 17:57.
            var data = await c.ProtocolDataAsync();
            var time = data.ExtraColumns.Single(x => x.Key == "weighted.time");
            var adj = data.ExtraColumns.Single(x => x.Key == "weighted.adj");
            Assert.Equal("17:56", time.Format(r["First"], new Dictionary<string, string>()));
            Assert.Equal("17:56", adj.Format(r["First"], new Dictionary<string, string>()));
            Assert.Equal("0:40:00", ShownTime.Result(r["First"].Team!.Time, r["First"].Team!.TimeDecimals));
        }
        finally
        {
            await catalog.SetEnabledAsync("weighted", wasOn);
        }
    }

    [Fact]
    public async Task Summary_SumsThePreciseDaysAndShowsTheTenthsOfATie()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("sumtenths"), dayCount: 2);
        await c.AddControlsAsync(1, "31");
        await c.AddControlsAsync(2, "31");
        await c.AddGroupAsync("Ч21", "31");
        await c.AddRunnerAsync("A", "1", "5001", "Ч21", T(10, 0));
        await c.AddRunnerAsync("B", "2", "5002", "Ч21", T(10, 0));
        await c.AddRunnerAsync("D", "3", "5003", "Ч21", T(10, 0));
        await c.AddReadoutAsync(1, "5001", "31", T(10, 0), T(10, 10, 0, 600));
        await c.AddReadoutAsync(2, "5001", "31", T(10, 0), T(10, 10, 0, 600));   // A: 20:01.2
        await c.AddReadoutAsync(1, "5002", "31", T(10, 0), T(10, 10, 0, 400));
        await c.AddReadoutAsync(2, "5002", "31", T(10, 0), T(10, 10, 0, 900));   // B: 20:01.3
        await c.AddReadoutAsync(1, "5003", "31", T(10, 0), T(10, 9, 59, 900));
        await c.AddReadoutAsync(2, "5003", "31", T(10, 0), T(10, 9, 59, 900));   // D: 19:59.8

        await c.SelectAsync();
        var data = await Sp.GetRequiredService<ICompetitionEditorService>().GetSummaryProtocolDataAsync();
        var ranked = Sp.GetRequiredService<ISummaryProtocolBuilder>()
            .RankGroups(data, new SummaryProtocolSettings { Mode = SummaryMode.ByTime, NoPriorityDay = true });
        var e = Assert.Single(ranked).Entries.ToDictionary(x => x.Member.FullName);

        Assert.Equal(T(0, 19, 59, 800), e["D"].TotalTime);       // the precise sum…
        Assert.Equal("00:19:59", e["D"].TotalText);               // …shown with the fraction dropped
        Assert.Equal(T(0, 20, 1, 200), e["A"].TotalTime);
        Assert.Equal([1, 2, 3], new[] { e["D"].Place!.Value, e["A"].Place!.Value, e["B"].Place!.Value });
        Assert.Equal("00:20:01.2", e["A"].TotalText);             // same second as B, another place → tenths
        Assert.Equal("00:20:01.3", e["B"].TotalText);
    }
}
