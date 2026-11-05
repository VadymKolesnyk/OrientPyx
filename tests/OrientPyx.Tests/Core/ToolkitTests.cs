using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.BusinessLogic.Services;
using OrientPyx.Plugins.Sdk.Toolkit.Patterns;

namespace OrientPyx.Tests.Core;

/// <summary>The pure algorithms of the plugin SDK toolkit and the business-logic helpers: no database, no UI.</summary>
public sealed class ToolkitTests
{
    // ---- CourseMatcher / CoursePattern -------------------------------------------------------------------

    [Fact]
    public void MatchOrder_AcceptsSubsequenceAndIgnoresExtras()
    {
        var m = CourseMatcher.MatchOrder(["31", "32", "33"], ["99", "31", "40", "32", "33", "41"]);
        Assert.True(m.Ok);
        Assert.Null(m.FirstMissing);
        Assert.Empty(m.Missed);
        Assert.Equal(3, m.Depth);
    }

    [Fact]
    public void MatchOrder_WrongOrderReportsFirstMissingAndMaximumMatch()
    {
        var m = CourseMatcher.MatchOrder(["31", "32", "33", "34"], ["31", "33", "32", "34"]);
        Assert.False(m.Ok);
        // Greedy walk: 31, then 32 (found after skipping 33), then 33 is gone — so 33 is the first missing.
        Assert.Equal("33", m.FirstMissing);
        // The maximum match keeps 31 33 34 (or 31 32 34): exactly one control is missed.
        Assert.Single(m.Missed);
    }

    [Fact]
    public void MatchOrder_IsCaseInsensitiveAndTrims()
    {
        Assert.True(CourseMatcher.MatchOrder(["a1", "B2"], [" A1 ", "b2"]).Ok);
    }

    [Fact]
    public void MatchOrder_EmptyCourseIsAlwaysOk()
    {
        Assert.True(CourseMatcher.MatchOrder([], ["31"]).Ok);
        Assert.True(CourseMatcher.MatchOrder([], []).Ok);
    }

    [Fact]
    public void CoursePattern_ParsesOrderedAndAnyOfBlocks()
    {
        var p = CoursePattern.Parse("S1 <41 42> [2: 45 46 47] F1");
        Assert.True(p.IsValid);
        Assert.Equal(4, p.RequiredCount);
        Assert.Equal(["41", "42", "45", "46", "47"], p.ControlCodes);
        Assert.Equal("S <41 42> [2: 45 46 47] F", p.NormalizedOrder());
    }

    [Theory]
    [InlineData("41 42 45 46", true)]
    [InlineData("41 42 47 45", true)]
    [InlineData("42 41 45 46", false)] // ordered run broken
    [InlineData("41 42 45", false)]    // only one of the two required any-of controls
    public void CoursePattern_MatchFollowsTheGrammar(string punches, bool ok)
    {
        var p = CoursePattern.Parse("<41 42> [2: 45 46 47]");
        var result = p.Match(punches.Split(' '), out var onCourse, out var firstMissing);
        Assert.Equal(ok, result);
        Assert.Equal(ok, firstMissing.Length == 0);
        Assert.Equal(punches.Split(' ').Length, onCourse.Length);
    }

    [Fact]
    public void CoursePattern_ReportsSyntaxErrors()
    {
        Assert.Contains(CoursePattern.Parse("<41 42").Errors, e => e.Kind == CoursePatternErrorKind.UnbalancedBracket);
        Assert.Contains(CoursePattern.Parse("[45 46]").Errors, e => e.Kind == CoursePatternErrorKind.ChoiceMissingColon);
        Assert.Contains(CoursePattern.Parse("[5: 45 46]").Errors, e => e.Kind == CoursePatternErrorKind.ChoiceCountTooLarge);
        Assert.Contains(CoursePattern.Parse("<>").Errors, e => e.Kind == CoursePatternErrorKind.EmptyOrderedBlock);
    }

    [Fact]
    public void CoursePattern_BlankMatchesAnythingAndEnumeratesOrders()
    {
        Assert.True(CoursePattern.Parse("").Match(["31"], out _, out _));
        var set = CoursePattern.Parse("[2: 45 46 47] 48").EnumerateOrders();
        Assert.Equal(6, set.TotalCount); // 3·2 ordered picks of two
        Assert.False(set.Truncated);
        Assert.All(set.Variants, v => Assert.Equal("48", v.Controls[^1]));
    }

    [Fact]
    public void MatchPattern_IgnoredCodesCountAsTaken()
    {
        var p = CoursePattern.Parse("31 32 33");
        Assert.False(CourseMatcher.MatchPattern(p, ["31", "33"]).Ok);
        Assert.True(CourseMatcher.MatchPattern(p, ["31", "33"], new HashSet<string> { "32" }).Ok);
    }

    [Fact]
    public void AlignOrder_GivesPrescribedIndexPerPunch()
    {
        var idx = CourseMatcher.AlignOrder(["31", "99", "32", "33"], ["31", "32", "33"]);
        Assert.Equal([0, -1, 1, 2], idx);
    }

    // ---- VariantMatcher ------------------------------------------------------------------------------------

    [Fact]
    public void VariantMatcher_PicksTheFullMatchThenTheDeepest()
    {
        var a = new CourseVariantData("A", ["31", "32", "33"]);
        var b = new CourseVariantData("B", ["33", "32", "31"]);
        Assert.Equal("B", VariantMatcher.Best([a, b], ["33", "32", "31"]).Variant.Code);
        var partial = VariantMatcher.Best([a, b], ["31", "32"]);
        Assert.Equal("A", partial.Variant.Code);
        Assert.False(partial.Ok);
        Assert.Equal("33", partial.FirstMissing);
        Assert.Throws<ArgumentException>(() => VariantMatcher.Best([], ["31"]));
    }

    [Fact]
    public void VariantMatcher_PatternVariantTreatsUnknownPatternCodesAsDisabled()
    {
        var v = new CourseVariantData("P", ["31", "33"], "<31 32 33>");
        Assert.True(v.IsPattern);
        Assert.True(VariantMatcher.Match(v, ["31", "33"]).Ok);
    }

    // ---- Ranking -------------------------------------------------------------------------------------------

    [Fact]
    public void Ranking_TiesShareAPlaceAndTheNextOneSkips()
    {
        var places = Ranking.AssignPlaces(
            new[] { ("a", 100), ("b", 90), ("c", 100), ("d", 80) },
            Ranking.ByTime<(string, int)>(x => TimeSpan.FromSeconds(x.Item2)));
        var byName = places.ToDictionary(p => p.Item.Item1, p => p.Place);
        Assert.Equal(1, byName["d"]);
        Assert.Equal(2, byName["b"]);
        Assert.Equal(3, byName["a"]);
        Assert.Equal(3, byName["c"]);
    }

    [Fact]
    public void Ranking_ExcludesOutOfCompetitionAndSortsNullTimeLast()
    {
        var places = Ranking.AssignPlaces(
            new[] { ("x", (TimeSpan?)null), ("y", TimeSpan.FromMinutes(1)), ("z", TimeSpan.FromMinutes(2)) },
            Ranking.ByTime<(string, TimeSpan?)>(x => x.Item2),
            x => x.Item1 == "z");
        Assert.Equal(["y", "x"], places.Select(p => p.Item.Item1));
        Assert.Equal([1, 2], places.Select(p => p.Place));
    }

    [Fact]
    public void Ranking_ByScoreThenTime()
    {
        var order = Ranking.ByScoreThenTime<(int Score, TimeSpan Time)>(x => x.Score, x => x.Time);
        var sorted = new[] { (50, TimeSpan.FromMinutes(70)), (60, TimeSpan.FromMinutes(90)), (60, TimeSpan.FromMinutes(80)) }
            .OrderBy(x => x, order).ToList();
        Assert.Equal([(60, TimeSpan.FromMinutes(80)), (60, TimeSpan.FromMinutes(90)), (50, TimeSpan.FromMinutes(70))], sorted);
    }

    // ---- RelayBib / relay helpers ---------------------------------------------------------------------------

    [Theory]
    [InlineData("100.2", true, 100, 2)]
    [InlineData("100,3", true, 100, 3)]
    [InlineData("7/1", true, 7, 1)]
    [InlineData("100", true, 100, null)]
    [InlineData("100.0", false, 0, null)]
    [InlineData("abc", false, 0, null)]
    [InlineData("", false, 0, null)]
    public void RelayBib_Parses(string text, bool ok, int team, int? leg)
    {
        Assert.Equal(ok, RelayBib.TryParse(text, out var t, out var l));
        Assert.Equal(team, t);
        Assert.Equal(leg, l);
    }

    [Fact]
    public void RelayBib_FormatsAndDetectsLegNumbers()
    {
        Assert.Equal("100.2", RelayBib.Format(100, 2));
        Assert.True(RelayBib.IsLegNumber("12.3"));
        Assert.False(RelayBib.IsLegNumber("12"));
    }

    // ---- SiCard --------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("1", "SI-Card 5", 30)]
    [InlineData("499999", "SI-Card 5", 30)]
    [InlineData("500000", "SI-Card 6", 64)]
    [InlineData(" 1234567 ", "SI-Card 9", 50)]
    [InlineData("2000001", "SI-Card 8", 30)]
    [InlineData("4500000", "pCard", 20)]
    [InlineData("6000000", "tCard", 25)]
    [InlineData("7001234", "SI-Card 10", 128)]
    [InlineData("8123456", "SIAC", 128)]
    [InlineData("9007400", "SI-Card 11", 128)]
    [InlineData("14000001", "fCard", 20)]
    [InlineData("16711680", "SI-Card 6*", 192)]
    public void SiCard_IdentifiesModelAndCapacityByNumberRange(string number, string model, int capacity)
    {
        var card = SiCard.Identify(number);
        Assert.NotNull(card);
        Assert.Equal(model, card.Model);
        Assert.Equal(capacity, card.Capacity);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0")]
    [InlineData("abc")]
    [InlineData("3000000")]   // unassigned range
    [InlineData("5000000")]
    [InlineData("10000000")]
    [InlineData("-5")]
    public void SiCard_UnknownNumbersAreNull(string number) => Assert.Null(SiCard.Identify(number));

    [Fact]
    public void SiCard_TooSmallJudgesOnlyKnownCardsAgainstAPositiveNeed()
    {
        Assert.Equal("64 (192)", SiCard.Identify("500000")!.CapacityText);
        Assert.Equal("50", SiCard.Identify("1000000")!.CapacityText);

        Assert.Equal("SI-Card 5", SiCard.TooSmall("123", 31)!.Model);
        // A 30-punch card keeps a reserve of 2: 29 and 28 controls are flagged, 27 fits.
        Assert.Equal("SI-Card 5", SiCard.TooSmall("123", 30)!.Model);
        Assert.Equal("SI-Card 5", SiCard.TooSmall("123", 29)!.Model);
        Assert.Equal("SI-Card 5", SiCard.TooSmall("123", 28)!.Model);
        Assert.Null(SiCard.TooSmall("123", 27));      // fits with the reserve
        Assert.Null(SiCard.TooSmall("123", 0));       // nothing needed
        Assert.Null(SiCard.TooSmall("3000000", 999)); // unknown card is never judged
        Assert.Null(SiCard.TooSmall("", 10));
    }

    [Fact]
    public void ChipCapacity_NeededPunchesIsTheLongerOfCourseAndVariants()
    {
        var row = new GroupDayRow(Guid.NewGuid(), Guid.NewGuid(), 0, "Ч21", "31 32 33", null, null,
            DisciplineId.SetCourse, null, null, null, ScatterMaxControlCount: 5);
        var strategy = new SetCourseStrategy(new CourseDistanceCalculator());
        Assert.Equal(5, ChipCapacity.NeededPunches(row, strategy));
        Assert.Equal(3, ChipCapacity.NeededPunches(row with { ScatterMaxControlCount = 0 }, strategy));
        Assert.Equal(0, ChipCapacity.NeededPunches(row with { CourseOrder = "", ScatterMaxControlCount = 0 }, strategy));
    }

    [Fact]
    public void RelayRestartTimesAndLegCourses_RoundTrip()
    {
        var times = RelayRestartTimes.Parse("{\"2\":\"11:30:00\",\"3\":\"12:00:00\",\"1\":\"10:00:00\"}");
        Assert.Equal(2, times.Count); // leg 1 never restarts
        Assert.Equal(new TimeSpan(11, 30, 0), times[2]);
        Assert.Equal(times, RelayRestartTimes.Parse(RelayRestartTimes.Format(times)));
        Assert.Empty(RelayRestartTimes.Parse("not json"));
        Assert.Equal("", RelayRestartTimes.Format(new Dictionary<int, TimeSpan>()));

        var courses = RelayLegCourses.Parse("{\"1\":\"A\",\"2\":\"B\",\"3\":\" \"}");
        Assert.Equal(new Dictionary<int, string> { [1] = "A", [2] = "B" }, courses);
        Assert.Equal(["A", "B", "B"], RelayLegCourses.ParseSet("A, B;B"));
    }

    [Fact]
    public void TeamCourseAssignment_MinimisesTotalPenaltyWithLexicographicTies()
    {
        // Rows = legs, columns = course slots.
        var result = TeamCourseAssignment.Assign([[0, 5], [5, 0]]);
        Assert.Equal([0, 1], result);
        Assert.Equal([0, 1], TeamCourseAssignment.Assign([[1, 1], [1, 1]]));
        Assert.Empty(TeamCourseAssignment.Assign([]));
        Assert.Throws<ArgumentException>(() => TeamCourseAssignment.Assign([[1, 2], [1]]));
    }

    // ---- RangeTable ----------------------------------------------------------------------------------------

    [Fact]
    public void RangeTable_LooksUpRangesAndNearestRowOutside()
    {
        var columns = new[] { TableColumnDef.Text("range", "Вік"), TableColumnDef.Number("M", "K (Ч)") };
        var table = TableValue.FromRows(columns, ["8-10", "1,5"], ["11–14", "1.2"], ["70+", "1.9"], ["", "9"]);
        var ranges = RangeTable.FromSetting(table);
        Assert.Equal(3, ranges.Count);
        Assert.Equal(1.5, ranges.Lookup(9, "M"));
        Assert.Equal(1.2, ranges.Lookup(14, "m"));   // keys are TextKey-insensitive
        Assert.Equal(1.5, ranges.Lookup(3, "M"));    // below the first row
        Assert.Equal(1.9, ranges.Lookup(99, "M"));   // open last row
        Assert.Equal(1.2, ranges.Lookup(40, "M"));   // in the gap: closer row
        Assert.Null(ranges.Lookup(9, "F"));
        Assert.True(RangeTable.TryParseRange("10-8", out var min, out var max));
        Assert.Equal((8, 10), (min, max));
        Assert.True(RangeTable.TryParseRange("70+", out _, out var open));
        Assert.Equal(int.MaxValue, open);
    }

    // ---- TextKey / TimeFormat / CourseOrder / Punches / Age ------------------------------------------------

    [Fact]
    public void TextKey_FoldsLookAlikesAndWhitespace()
    {
        Assert.True(TextKey.Same("М12", "M12"));         // Cyrillic М vs Latin M
        Assert.True(TextKey.Same("  Київ  клуб ", "київ клуб"));
        Assert.Equal("", TextKey.Of(null));
        Assert.False(TextKey.Same("Ж21", "Ч21"));
        var set = new HashSet<string>(TextKey.Comparer) { "Ч21" };
        Assert.Contains("ч21", set);
    }

    [Fact]
    public void TimeFormat_FormatsDurationsAndResults()
    {
        Assert.Equal("5:07", TimeFormat.Duration(new TimeSpan(0, 5, 7)));
        Assert.Equal("1:05:07", TimeFormat.Duration(new TimeSpan(1, 5, 7)));
        Assert.Equal("", TimeFormat.Duration(TimeSpan.FromSeconds(-1)));
        Assert.Equal("0:35:02", TimeFormat.Result(new TimeSpan(0, 35, 2)));
        Assert.Equal("", TimeFormat.Result(null));
        Assert.Equal("00:10:30", TimeFormat.TotalHours(new TimeSpan(0, 10, 30)));
        Assert.NotEqual("", TimeFormat.Span(TimeSpan.FromSeconds(-61)));
    }

    [Theory]
    [InlineData("S1 31 32 33 F1", 3)]
    [InlineData("31 32 33", 3)]
    [InlineData("С1 31 Ф1", 1)]
    [InlineData("", 0)]
    [InlineData(null, 0)]
    public void CourseOrder_CountsControlsWithoutMarkers(string? order, int count)
    {
        Assert.Equal(count, CourseOrder.CountControls(order));
    }

    [Fact]
    public void CourseOrder_CountsPatternRequiredControls()
    {
        Assert.Equal(4, CourseOrder.CountCourseControls("<41 42> [2: 45 46 47]"));
    }

    [Fact]
    public void Punches_SplitAndFilter()
    {
        Assert.Equal(["31", "32"], Punches.Split("  31\t32 "));
        var sf = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "S1", "F1" };
        Assert.Equal(["31"], Punches.WithoutStartFinish(["S1", "31", "F1"], sf));
        Assert.Equal(["31"], Punches.WithoutDisabled(["31", "32"], new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "32" }));
    }

    [Fact]
    public void Age_InYear()
    {
        Assert.Equal(15, Age.InYear(2011, new DateOnly(2026, 6, 1)));
    }

    // ---- DisciplineId ---------------------------------------------------------------------------------------

    [Fact]
    public void DisciplineId_DefaultsToSetCourseAndComparesOrdinally()
    {
        Assert.Equal("SetCourse", default(DisciplineId).Value);
        Assert.Equal(DisciplineId.SetCourse, new DisciplineId(" SetCourse "));
        Assert.Equal(DisciplineId.SetCourse, new DisciplineId(""));
        Assert.NotEqual(DisciplineId.Relay, new DisciplineId("relay"));
        Assert.Equal("weighted.relay", new DisciplineId("weighted.relay").ToString());
    }

    // ---- Points ---------------------------------------------------------------------------------------------

    [Fact]
    public void PointsTable_ParsesAndFormats()
    {
        Assert.Equal([45m, 42m, 40m], PointsTable.Parse("[45.00,42.00,40.00]"));
        Assert.Empty(PointsTable.Parse("oops"));
        Assert.Equal([45m, 42.5m], PointsTable.Parse(PointsTable.Serialize([45m, 42.5m])));
        Assert.Equal(1.24m, PointsTable.Normalize(1.235m));
        Assert.Equal("1.50", PointsTable.Format(1.5m));
    }

    [Fact]
    public void PointsFormula_EvaluatesVariablesFunctionsAndDivisionByZero()
    {
        var ctx = new PointsFormulaContext { ParticipantTime = 1200, LeaderTime = 1000, GroupSize = 10, StartedCount = 9, FinishedCount = 8, Place = 2, Score = 50, LeaderScore = 60 };
        Assert.Equal(80, PointsFormula.Evaluate("100*(2 - T_у/T_л)", ctx));
        Assert.Equal(8, PointsFormula.Evaluate("N_ф", ctx));
        Assert.Equal(2, PointsFormula.Evaluate("Місце", ctx));
        Assert.Equal(60, PointsFormula.Evaluate("max(Бали; Бали_л)", ctx));
        Assert.Equal(1.23, PointsFormula.Evaluate("round(1,2345; 2)", ctx));
        Assert.Equal(0, PointsFormula.Evaluate("5/0", ctx));
        Assert.Throws<FormatException>(() => PointsFormula.Evaluate("1 +", ctx));
        Assert.False(PointsFormula.TryValidate("", out var error));
        Assert.Equal("empty", error);
        Assert.True(PointsFormula.TryValidate("min(N; 3) + 1", out _));
    }

    [Fact]
    public void PointsRuleEvaluator_TableAndFormulaEdges()
    {
        var table = new PointsRule { Kind = PointsRuleKind.Table, TableJson = "[10,7,5]" };
        Assert.Equal(7m, PointsRuleEvaluator.Evaluate(table, Input(place: 2)));
        Assert.Equal(0m, PointsRuleEvaluator.Evaluate(table, Input(place: 4)));
        Assert.Null(PointsRuleEvaluator.Evaluate(table, Input(place: null)));
        Assert.Null(PointsRuleEvaluator.Evaluate(null, Input(place: 1)));

        var formula = new PointsRule { Kind = PointsRuleKind.Formula, Formula = "100*(2 - T_у/T_л)" };
        Assert.Equal(80m, PointsRuleEvaluator.Evaluate(formula, Input(place: 2, time: 1200, leader: 1000)));
        Assert.Equal(0m, PointsRuleEvaluator.Evaluate(formula, Input(place: 5, time: 5000, leader: 1000))); // a negative result becomes 0
        Assert.Null(PointsRuleEvaluator.Evaluate(new PointsRule { Kind = PointsRuleKind.Formula, Formula = "(" }, Input(place: 1)));

        static PointsRuleInput Input(int? place, double? time = 600, double? leader = 600) =>
            new(place, time, null, leader, null, 10, 10, 10);
    }

    // ---- Rank qualification --------------------------------------------------------------------------------

    [Fact]
    public void RankQualification_AwardsByTimePercentAndByPoints()
    {
        var rows = new List<RankQualificationRow>
        {
            new() { Rank = 0, TimeKms = 105, TimeFirst = 115, TimeSecond = 130, TimeThird = 150, TimeThirdJunior = 180,
                    PointsKms = 95, PointsFirst = 85, PointsSecond = 70, PointsThird = 50, PointsThirdJunior = 30, Order = 0 },
            new() { Rank = 100, TimeKms = 110, TimeFirst = 125, TimeSecond = 140, TimeThird = 160, TimeThirdJunior = 190, Order = 1 },
        };
        Assert.Equal("КМСУ", RankQualificationEvaluator.Award(rows, 50, GroupRankLevel.Adult, pointsBased: false, percent: 104));
        Assert.Equal("II", RankQualificationEvaluator.Award(rows, 50, GroupRankLevel.Adult, false, 120));
        Assert.Null(RankQualificationEvaluator.Award(rows, 50, GroupRankLevel.Adult, false, 151));
        Assert.Equal("КМСУ", RankQualificationEvaluator.Award(rows, 150, GroupRankLevel.Adult, false, 109)); // the row with the largest Rank ≤ course rank applies
        Assert.Equal("I-ю", RankQualificationEvaluator.Award(rows, 50, GroupRankLevel.Junior, false, 125));
        Assert.Equal("I", RankQualificationEvaluator.Award(rows, 50, GroupRankLevel.Adult, pointsBased: true, percent: 90));
        Assert.Null(RankQualificationEvaluator.Award(rows, 50, GroupRankLevel.None, false, 100));
        Assert.Null(RankQualificationEvaluator.Award([], 50, GroupRankLevel.Adult, false, 100));
    }

    // ---- Settings -------------------------------------------------------------------------------------------

    [Fact]
    public void SettingValues_ResolveDefaultsAndConvert()
    {
        var defs = new[] { Setting.Int("n", "N", 5, min: 0), Setting.Bool("f", "F"), Setting.Choice("c", "C", ["a", "b"]) };
        var values = new SettingValues(new Dictionary<string, object?> { ["n"] = "7" }, defs);
        Assert.Equal(7, values.Get<int>("n"));
        Assert.False(values.Get<bool>("f"));
        Assert.Equal("a", values.Get<string>("c"));
        Assert.Equal(TimeSpan.FromMinutes(1), SettingValues.ConvertTo<TimeSpan>("00:01:00"));
        Assert.Equal(0, SettingValues.ConvertTo<int>("x"));
        Assert.Null(SettingValues.Empty["anything"]);
    }

    [Fact]
    public void DisciplineSettings_JsonRoundTripAndScopes()
    {
        var defs = new[] { Setting.Int("n", "N", 1), Setting.Decimal("d", "D", 1.5m, scope: SettingScope.Competition) };
        var json = DisciplineSettings.Serialize(new Dictionary<string, object?> { ["n"] = 3, ["d"] = 2.5m }, defs);
        var parsed = DisciplineSettings.Parse(json, defs);
        Assert.Equal(3, Convert.ToInt32(parsed["n"]));
        Assert.Equal(2.5m, Convert.ToDecimal(parsed["d"]));
        Assert.Empty(DisciplineSettings.Parse("garbage", defs));
    }

    // ---- Distances -----------------------------------------------------------------------------------------

    [Fact]
    public void CourseDistance_HaversineAndMap()
    {
        var calc = new CourseDistanceCalculator();
        var km = calc.TotalKilometres([new GeoPoint(50.0, 30.0), new GeoPoint(50.0, 30.01), new GeoPoint(null, 30.02)]);
        Assert.InRange(km, 0.70m, 0.73m); // ~0.715 km for 0.01° of longitude at 50°N; the missing leg adds 0
        Assert.Equal(0.300m, calc.TotalKilometresFromMap([new MapPoint(0, 0), new MapPoint(10, 0), new MapPoint(10, 10), new MapPoint(0, 10)], 10000));
        Assert.Equal(0m, calc.TotalKilometresFromMap([new MapPoint(0, 0), new MapPoint(10, 0)], 0));
        Assert.Equal(0m, calc.TotalKilometres([new GeoPoint(50, 30)]));
    }
}
