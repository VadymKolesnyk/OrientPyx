using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.BusinessLogic.Services;
using OrientPyx.Tests.Support;

namespace OrientPyx.Tests.Core;

/// <summary>The documents: result protocol rows and captions, relay and rogaine sub-rows, start protocols, the
/// statement order, the summary ranking (by points / by time), winners and the splits export.</summary>
[Collection(AppCollection.Name)]
public sealed class ProtocolBuilderTests
{
    private static IServiceProvider Sp => TestEnvironment.Instance.Services;

    private static TimeSpan T(int h, int m, int s = 0) => new(h, m, s);

    private static ProtocolLabels ResultLabels() => Labels.Make<ProtocolLabels>() with
    {
        ColumnHeaders = Enum.GetValues<ProtocolColumn>().ToDictionary(c => c, c => c.ToString()),
        DistanceLabel = "Довжина",
        ControlCountLabel = "КП",
        TimeLimitLabel = "Контрольний час",
    };

    private static int Col(ResultProtocolDocument doc, string header) => doc.ColumnHeaders.ToList().IndexOf(header);

    [Fact]
    public async Task ResultProtocol_RowsPlacesGapsAndCaptions()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("prot"));
        await c.AddControlsAsync(1, "31", "32");
        await c.AddGroupAsync("Ч21", "31 32", s => { s.DistanceKm = 4.25m; s.TimeLimitSeconds = 5400; s.CourseSetter = "Іваненко"; s.CourseSetterCategory = "НК"; });
        await c.AddRunnerAsync("Перший", "1", "1001", "Ч21", T(10, 0), p => p.BirthDate = new DateTimeOffset(1990, 2, 3, 0, 0, 0, TimeSpan.Zero));
        await c.AddRunnerAsync("Другий", "2", "1002", "Ч21", T(10, 0));
        await c.AddRunnerAsync("Гість", "3", "1003", "Ч21", T(10, 0), configureDay: d => d.OutOfCompetition = true);
        await c.AddRunnerAsync("Знятий", "4", "1004", "Ч21", T(10, 0));
        await c.AddRunnerAsync("Нечитаний", "5", "1005", "Ч21", T(10, 0));
        await c.AddReadoutAsync(1, "1001", "31 32", T(10, 0), T(10, 30));
        await c.AddReadoutAsync(1, "1002", "31 32", T(10, 0), T(10, 31, 30));
        await c.AddReadoutAsync(1, "1003", "31 32", T(10, 0), T(10, 20));
        await c.AddReadoutAsync(1, "1004", "31", T(10, 0), T(10, 25));

        var data = await c.ProtocolDataAsync();
        var settings = new ResultProtocolSettings();
        foreach (var col in settings.Columns)
            col.Visible = col.Column is ProtocolColumn.Sequence or ProtocolColumn.Number or ProtocolColumn.FullName or ProtocolColumn.BirthDate
                or ProtocolColumn.Result or ProtocolColumn.Place or ProtocolColumn.Gap;
        settings.Title = "Протокол";
        var doc = Sp.GetRequiredService<IResultProtocolBuilder>().Build(data, settings, ResultLabels());

        Assert.Equal("Протокол", doc.Title);
        var section = Assert.Single(doc.Sections);
        Assert.Equal("Ч21", section.GroupName);
        Assert.Equal("Довжина: 4,250 км".Replace(',', '.'), section.DistanceText.Replace(',', '.'));
        Assert.Equal("2 КП", section.ControlCountText);
        Assert.Contains("1:30:00", section.TimeLimitText);
        Assert.Contains("Іваненко (НК)", section.CourseSetterText);

        var seq = Col(doc, "Sequence"); var name = Col(doc, "FullName"); var result = Col(doc, "Result");
        var place = Col(doc, "Place"); var gap = Col(doc, "Gap"); var birth = Col(doc, "BirthDate");
        var rows = section.Rows.Select(r => r.Cells).ToList();
        Assert.Equal(5, rows.Count);
        Assert.Equal(["Перший", "Другий", "Гість", "Знятий", "Нечитаний"], rows.Select(r => r[name]));
        Assert.Equal(["1", "2", "3", "4", "5"], rows.Select(r => r[seq]));
        Assert.Equal("0:30:00", rows[0][result]);
        Assert.Equal("1", rows[0][place]);
        Assert.Equal("", rows[0][gap]);
        Assert.Equal("+0:01:30", rows[1][gap]);
        Assert.Equal("П/К", rows[2][place]);
        Assert.Equal("MP", rows[3][result]);
        Assert.Equal("", rows[4][result]);
        Assert.Equal("03.02.1990", rows[0][birth]);
        Assert.NotNull(doc.Footer);
    }

    [Fact]
    public async Task ResultProtocol_RelayTeamsBecomeHeaderAndLegRows()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("protr"), discipline: DisciplineId.Relay);
        await c.AddControlsAsync(1, "31");
        await c.AddGroupAsync("R", "31", s => { s.LegCount = 2; s.MassStartTime = T(10, 0); });
        await c.AddRunnerAsync("A1", "", "6011", "R");
        await c.AddRunnerAsync("A2", "", "6012", "R");
        await c.AddRunnerAsync("B1", "", "6021", "R");
        await c.AddRelayTeamAsync("R", 601, "Alpha", 1, ("A1", 1), ("A2", 2));
        await c.AddRelayTeamAsync("R", 602, "Beta", 1, ("B1", 1));
        await c.AddReadoutAsync(1, "6011", "31", null, T(10, 20));
        await c.AddReadoutAsync(1, "6012", "31", null, T(10, 45));
        await c.AddReadoutAsync(1, "6021", "31", null, T(10, 15));

        var data = await c.ProtocolDataAsync();
        var labels = ResultLabels() with { RelayVacantLeg = "— етап не призначено", RelayPendingMark = "очікує" };
        var relaySettings = new ResultProtocolSettings();
        relaySettings.Columns.Single(x => x.Column == ProtocolColumn.Number).Visible = true;
        var doc = Sp.GetRequiredService<IResultProtocolBuilder>().Build(data, relaySettings, labels);
        var section = Assert.Single(doc.Sections);
        var name = Col(doc, "FullName"); var number = Col(doc, "Number"); var result = Col(doc, "Result"); var place = Col(doc, "Place");
        var rows = section.Rows;
        Assert.Equal(6, rows.Count);
        Assert.True(rows[0].IsTeamHeader);
        Assert.Equal("Alpha", rows[0].Cells[name]);
        Assert.Equal("0:45:00", rows[0].Cells[result]);
        Assert.Equal("1", rows[0].Cells[place]);
        Assert.True(rows[1].IsSubRow);
        Assert.Equal("601.1", rows[1].Cells[number]);
        Assert.Equal("A1", rows[1].Cells[name]);
        Assert.Equal("0:20:00", rows[1].Cells[result]);
        Assert.Equal("601.2", rows[2].Cells[number]);
        Assert.True(rows[3].IsTeamHeader);
        Assert.Equal("Beta", rows[3].Cells[name]);
        Assert.Equal("П/К", rows[3].Cells[place]);                // incomplete team
        Assert.Equal("— етап не призначено", rows[5].Cells[name]);
    }

    [Fact]
    public async Task ResultProtocol_RogaineTeamsBecomeHeaderAndMemberRows()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("protg"), discipline: DisciplineId.Rogaine);
        var cps = await c.AddControlsAsync(1, "31", "42");
        var store = Sp.GetRequiredService<IEventStore>();
        foreach (var cp in cps) { cp.Points = int.Parse(cp.Code[..1]); await store.UpdateControlPointAsync(c.Folder, cp); }
        await c.AddGroupAsync("Open", "31 42", s => s.TimeLimitSeconds = 3600);
        await c.AddRunnerAsync("X1", "1", "7011", "Open", T(10, 0), p => p.Team = "Xena");
        await c.AddRunnerAsync("X2", "2", "7012", "Open", T(10, 0), p => p.Team = "Xena");
        await c.AddRunnerAsync("Y1", "3", "7021", "Open", T(10, 0), p => p.Team = "Yak");
        await c.AddReadoutAsync(1, "7011", "31 42", T(10, 0), T(10, 50));
        await c.AddReadoutAsync(1, "7012", "42 31", T(10, 0), T(10, 55));
        await c.AddReadoutAsync(1, "7021", "31", T(10, 0), T(10, 30));

        var data = await c.ProtocolDataAsync();
        Assert.True(data.Groups[0].IsTeam);
        var doc = Sp.GetRequiredService<IResultProtocolBuilder>().Build(data, new ResultProtocolSettings(), ResultLabels());
        var rows = Assert.Single(doc.Sections).Rows;
        var name = Col(doc, "FullName"); var score = Col(doc, "Score"); var place = Col(doc, "Place");
        Assert.True(rows[0].IsTeamHeader);
        Assert.Equal("Xena", rows[0].TeamName);
        Assert.Equal("7", rows[0].Cells[score]);
        Assert.Equal("1", rows[0].Cells[place]);
        Assert.False(rows[1].IsTeamHeader);
        Assert.Equal(["X1", "X2"], new[] { rows[1].Cells[name], rows[2].Cells[name] });
        Assert.Equal("", rows[1].Cells[place]);
        Assert.True(rows[3].IsTeamHeader);
        Assert.Equal("Yak", rows[3].TeamName);
        // The team row's time is the team's (its slowest member, 0:55), not whoever is listed first (X1, 0:50).
        var result = Col(doc, "Result");
        Assert.Equal(rows[2].Cells[result], rows[0].Cells[result]);
    }

    [Fact]
    public async Task Protocol_LeavesOutTheScoreColumnWhenNobodyHasAScore()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("noscore"));
        await c.AddControlsAsync(1, "31");
        await c.AddGroupAsync("Ч21", "31");
        await c.AddRunnerAsync("A", "1", "1001", "Ч21", T(10, 0));
        await c.AddReadoutAsync(1, "1001", "31", T(10, 0), T(10, 20));

        var doc = Sp.GetRequiredService<IResultProtocolBuilder>().Build(
            await c.ProtocolDataAsync(), new ResultProtocolSettings(), ResultLabels());
        Assert.DoesNotContain("Score", doc.ColumnHeaders); // set course: no «Бали» even though the template ticks it
        Assert.Contains("Result", doc.ColumnHeaders);
    }

    [Fact]
    public async Task StartProtocols_OrderByStartTimeAndByMinute()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("sp"));
        await c.AddGroupAsync("Ч21", "31");
        await c.AddGroupAsync("Ж21", "31");
        await c.AddRunnerAsync("C-late", "1", "", "Ч21", T(10, 2));
        await c.AddRunnerAsync("C-early", "2", "", "Ч21", T(10, 0));
        await c.AddRunnerAsync("C-none", "3", "", "Ч21", null);
        await c.AddRunnerAsync("W-same", "4", "", "Ж21", T(10, 0));
        await c.SelectAsync();
        var editor = Sp.GetRequiredService<ICompetitionEditorService>();
        var data = await editor.GetStartProtocolDataAsync(c.Day(1).Id);
        var builder = Sp.GetRequiredService<IStartProtocolBuilder>();
        var labels = Labels.Make<StartProtocolLabels>() with
        {
            ColumnHeaders = Enum.GetValues<StartProtocolColumn>().ToDictionary(x => x, x => x.ToString()),
            NoStartTimeCaption = "Без часу старту",
        };

        var regular = builder.Build(data, StartProtocolSettings.Default(StartProtocolKind.Regular), StartProtocolKind.Regular, labels);
        var nameCol = regular.ColumnHeaders.ToList().IndexOf("FullName");
        var ch = regular.Sections.Single(s => s.GroupName == "Ч21");
        Assert.Equal(["C-early", "C-late", "C-none"], ch.Rows.Select(r => r.Cells[nameCol]));

        var judges = builder.Build(data, StartProtocolSettings.Default(StartProtocolKind.Judges), StartProtocolKind.Judges, labels);
        var jName = judges.ColumnHeaders.ToList().IndexOf("FullName");
        Assert.True(judges.Sections.Count >= 3);                     // 10:00, 10:02 and the "no time" tail
        var first = judges.Sections[0];
        Assert.Equal(2, first.Rows.Count);                            // both groups' 10:00 starters
        Assert.Contains("C-early", first.Rows.Select(r => r.Cells[jName]));
        Assert.Contains("W-same", first.Rows.Select(r => r.Cells[jName]));
        Assert.Equal("Без часу старту", judges.Sections[^1].GroupName);
        Assert.Equal("C-none", judges.Sections[^1].Rows.Single().Cells[jName]);
    }

    [Fact]
    public async Task Statement_SortsChipsFirstRentalBeforeOwnAndBoldsOwnChips()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("stmt"));
        await c.AddGroupAsync("Ч21", "31");
        var store = Sp.GetRequiredService<IEventStore>();
        await store.AddRentalChipAsync(c.Folder, new RentalChip { Number = "5000" });
        await c.AddRunnerAsync("Own", "1", "9000", "Ч21");
        await c.AddRunnerAsync("Rental", "2", "5000", "Ч21");
        await c.AddRunnerAsync("None", "3", "", "Ч21");
        await c.SelectAsync();
        var editor = Sp.GetRequiredService<ICompetitionEditorService>();
        var roster = await editor.GetParticipantRosterAsync();
        var data = await editor.GetStatementDataAsync(null, roster.Select(r => r.ParticipantId).ToList());
        var settings = new StatementSettings();
        var labels = Labels.Make<StatementLabels>() with
        {
            ColumnHeaders = Enum.GetValues<StatementColumn>().ToDictionary(x => x, x => x.ToString()),
        };
        var doc = Sp.GetRequiredService<IStatementBuilder>().Build(data, settings, labels, "усі");
        var section = Assert.Single(doc.Sections);
        var name = doc.ColumnHeaders.ToList().IndexOf("FullName");
        var chip = doc.ColumnHeaders.ToList().IndexOf("Chip");
        Assert.Equal(["Rental", "Own", "None"], section.Rows.Select(r => r.Cells[name]));
        Assert.True(section.Rows[1].BoldCells![chip]);
        Assert.Null(section.Rows[0].BoldCells);
        Assert.Equal("усі", doc.FilterSummary);

        // By number: numeric bibs ascending (9 before 10), a text bib after them, no bib last.
        var rows = new[]
        {
            new StatementRow { Number = "10", FullName = "Ten" },
            new StatementRow { Number = "", FullName = "Blank" },
            new StatementRow { Number = "9", FullName = "Nine" },
            new StatementRow { Number = "A1", FullName = "Text" },
        };
        Assert.Equal(["Nine", "Ten", "Text", "Blank"],
            StatementBuilder.Order(rows, StatementSortOrder.Number).Select(r => r.FullName));
        settings.SortOrder = StatementSortOrder.Number;
        doc = Sp.GetRequiredService<IStatementBuilder>().Build(data, settings, labels, "усі");
        Assert.Equal(["Own", "Rental", "None"], doc.Sections[0].Rows.Select(r => r.Cells[name]));
    }

    [Fact]
    public async Task Summary_RanksByPointsAndByTimeWithPriorityDay()
    {
        var app = Sp.GetRequiredService<IAppStore>();
        var table = await app.AddPointsRuleAsync(PointsRuleKind.Table);
        table.Name = "summary"; table.TableJson = "[45,42,40]";
        await app.UpdatePointsRuleAsync(table);

        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("sum"), dayCount: 2);
        await c.AddControlsAsync(1, "31");
        await c.AddControlsAsync(2, "31");
        await c.AddGroupAsync("Ч21", "31", s => s.PointsRuleId = table.Id);
        await c.AddRunnerAsync("A", "1", "1001", "Ч21", T(10, 0));
        await c.AddRunnerAsync("B", "2", "1002", "Ч21", T(10, 0));
        await c.AddRunnerAsync("C", "3", "1003", "Ч21", T(10, 0));
        await c.AddRunnerAsync("OneDay", "4", "1004", "Ч21", T(10, 0));
        // Day 1: A 1st (45), B 2nd (42), C 3rd (40). Day 2: C 1st, B 2nd, A 3rd. OneDay runs only day 1 (4th → 0).
        await c.AddReadoutAsync(1, "1001", "31", T(10, 0), T(10, 10));
        await c.AddReadoutAsync(1, "1002", "31", T(10, 0), T(10, 11));
        await c.AddReadoutAsync(1, "1003", "31", T(10, 0), T(10, 12));
        await c.AddReadoutAsync(1, "1004", "31", T(10, 0), T(10, 13));
        await c.AddReadoutAsync(2, "1003", "31", T(10, 0), T(10, 10));
        await c.AddReadoutAsync(2, "1002", "31", T(10, 0), T(10, 11));
        await c.AddReadoutAsync(2, "1001", "31", T(10, 0), T(10, 12));

        await c.SelectAsync();
        var editor = Sp.GetRequiredService<ICompetitionEditorService>();
        var data = await editor.GetSummaryProtocolDataAsync();
        var builder = Sp.GetRequiredService<ISummaryProtocolBuilder>();

        var byPoints = builder.RankGroups(data, new SummaryProtocolSettings { Mode = SummaryMode.ByPoints });
        var g = Assert.Single(byPoints);
        var entries = g.Entries.ToDictionary(e => e.Member.FullName);
        Assert.Equal(1, entries["A"].Place);                     // 85 with the priority (first) day 45 beats C's 40
        Assert.Equal(2, entries["C"].Place);
        Assert.Equal(3, entries["B"].Place);                     // 84
        Assert.Equal(4, entries["OneDay"].Place);                // one counted day still ranks in points mode
        Assert.Equal("85.00", entries["A"].TotalText);

        var noPriority = builder.RankGroups(data, new SummaryProtocolSettings { Mode = SummaryMode.ByPoints, NoPriorityDay = true });
        var np = noPriority[0].Entries.ToDictionary(e => e.Member.FullName);
        Assert.Equal(1, np["A"].Place);
        Assert.Equal(1, np["C"].Place);
        Assert.Equal(3, np["B"].Place);

        var requireAll = builder.RankGroups(data, new SummaryProtocolSettings { Mode = SummaryMode.ByPoints, RequireAllDays = true });
        Assert.Null(requireAll[0].Entries.Single(e => e.Member.FullName == "OneDay").Place);

        var byTime = builder.RankGroups(data, new SummaryProtocolSettings { Mode = SummaryMode.ByTime, NoPriorityDay = true });
        var bt = byTime[0].Entries.ToDictionary(e => e.Member.FullName);
        Assert.Equal(1, bt["A"].Place);                          // 22:00
        Assert.Equal(1, bt["B"].Place);                          // 22:00
        Assert.Equal(1, bt["C"].Place);                          // 22:00
        Assert.Null(bt["OneDay"].Place);                         // time mode needs every day
        Assert.Equal("00:22:00", bt["A"].TotalText);

        var doc = builder.Build(data, new SummaryProtocolSettings(), Labels.Make<SummaryProtocolLabels>());
        Assert.Equal(2, doc.DayBands.Count);
        Assert.Equal(3, doc.DayBands[0].SubColumns.Count);
        Assert.Single(doc.Sections);
        Assert.Equal(4, doc.Sections[0].Rows.Count);
    }

    [Fact]
    public async Task Winners_KeepTiesWholeAndSkipGroupsWithoutPlaces()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("win"));
        await c.AddControlsAsync(1, "31");
        await c.AddGroupAsync("Ч21", "31");
        await c.AddGroupAsync("Empty", "31");
        for (var i = 1; i <= 5; i++)
        {
            await c.AddRunnerAsync($"R{i}", $"{i}", $"80{i}", "Ч21", T(10, 0));
            await c.AddReadoutAsync(1, $"80{i}", "31", T(10, 0), T(10, i <= 2 ? 10 : 10 + i)); // R1, R2 tie for 1st
        }
        var data = await c.ProtocolDataAsync();
        var labels = new WinnersPrintLabels(p => $"{p} місце", (n, p) => $"{p} місце ({n})");
        var doc = Sp.GetRequiredService<IWinnersPrintBuilder>().BuildForDay(data, new WinnersPrintHeader("Кубок", "Переможці", "06.06.2026"), labels, 3);
        Assert.False(doc.IsEmpty);
        var group = Assert.Single(doc.Groups);
        Assert.Equal([1, 3], group.Places.Select(p => p.Place));   // 1 (shared), then 3; nobody is 2nd
        Assert.Equal(["R1", "R2"], group.Places[0].Winners.Select(w => w.FullName));
        Assert.True(group.Places[0].IsShared);
        Assert.Equal("1 місце (2)", group.Places[0].Heading);
        Assert.Equal("0:00:10:00".Replace("0:00:", "0:"), group.Places[0].Winners[0].ResultText);
        Assert.Equal(["R3"], group.Places[1].Winners.Select(w => w.FullName));
    }

    [Fact]
    public async Task SplitsExport_RowsCarryResultPlaceAndPassage()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("splt"));
        await c.AddControlsAsync(1, "31", "32");
        await c.AddGroupAsync("Ч21", "31 32");
        await c.AddRunnerAsync("Fast", "1", "1001", "Ч21", T(10, 0));
        await c.AddRunnerAsync("Mp", "2", "1002", "Ч21", T(10, 0));
        await c.AddRunnerAsync("Unread", "3", "1003", "Ч21", T(10, 0));
        await c.AddReadoutAsync(1, "1001", "31 32", T(10, 0), T(10, 20));
        await c.AddReadoutAsync(1, "1002", "32", T(10, 0), T(10, 20));
        await c.SelectAsync();
        var editor = Sp.GetRequiredService<ICompetitionEditorService>();
        var data = await editor.GetDaySplitsExportDataAsync(c.Day(1).Id);
        var group = Assert.Single(data.Groups);
        Assert.Equal(2, group.Rows.Count);                          // only read chips
        Assert.Equal(SplitsLayout.Ordered, group.Layout);
        Assert.Equal(["31", "32"], group.Controls);

        var doc = Sp.GetRequiredService<ISplitExportBuilder>().Build(data, Labels.Make<SplitExportHeader>(), Labels.Make<SplitExportLabels>());
        var rows = doc.Groups[0].Rows;
        Assert.Equal("Fast", rows[0].FullName);
        Assert.Equal("0:20:00", rows[0].ResultText);
        Assert.Equal("1", rows[0].PlaceText);
        Assert.True(rows[0].IsOk);
        Assert.Equal(2, rows[0].Splits.Expected.Count);
        Assert.All(rows[0].Splits.Expected, e => Assert.True(e.Taken));
        Assert.Equal("MP", rows[1].StatusText);
        Assert.False(rows[1].IsOk);
        Assert.False(rows[1].Splits.Expected.Single(e => e.Code == "31").Taken);
        Assert.Equal(new TimeSpan(0, 6, 40), rows[0].Splits.Passage.First(p => p.Code == "31").Leg); // 20 min spread over 3 legs
    }
}
