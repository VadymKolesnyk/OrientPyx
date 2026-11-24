using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Tests.Support;

namespace OrientPyx.Tests.Core;

/// <summary>
/// The IOF 3.0 builder on hand-made data (docs/iof-xml-export-plan.md §7.1): the status table, the time line (midnight,
/// a Sport Time stamp's synthetic date and winter offset), names, splits (prescribed and free order), the relay team
/// shape and the runner's course — and every document stays valid against the official schema.
/// </summary>
[Collection(AppCollection.Name)]
public sealed class IofExportBuilderTests
{
    private static readonly TimeSpan Kyiv = TimeSpan.FromHours(3);
    private static readonly DateOnly Day = new(2026, 7, 4);

    private static IServiceProvider Sp => TestEnvironment.Instance.Services;
    private static IIofExportBuilder Builder => Sp.GetRequiredService<IIofExportBuilder>();
    private static IIofXmlWriter Writer => Sp.GetRequiredService<IIofXmlWriter>();

    private static IofExportOptions Options => Checks.IofOptions;

    private static DateTimeOffset At(int h, int m, int s = 0, int dayShift = 0) =>
        new DateTimeOffset(Day.ToDateTime(TimeOnly.MinValue), Kyiv).AddDays(dayShift) + new TimeSpan(h, m, s);

    private static IofRaceData Race(bool locked = false) => new(Guid.NewGuid(), 1, Day, Kyiv, locked);

    private static ParticipantDayResult Result(
        FinishStatus status, DateTimeOffset? start, DateTimeOffset? finish, int? place = null, bool readout = true,
        bool outOfCompetition = false) =>
        new(start, finish, status, null, status,
            status == FinishStatus.Ok && start is { } s && finish is { } f ? f - s : null, place, null, readout)
        {
            OutOfCompetition = outOfCompetition
        };

    private static ResultProtocolRow Row(string name, ParticipantDayResult result, Guid id, string team = "", string number = "1") =>
        new(number, name, new DateTimeOffset(1990, 3, 1, 0, 0, 0, TimeSpan.Zero), "Азимут", "Київська", "", "", "", team, result)
        {
            ParticipantId = id
        };

    private static IofRunnerData Runner(DateTimeOffset? start, SplitsView? splits = null, IofRunnerCourse? course = null) =>
        new("", "8001", null, start, splits, course);

    private static IofExportData OneGroup(
        IofRaceData race, IofClassInfo info, IReadOnlyList<ResultProtocolRow> rows,
        IReadOnlyDictionary<Guid, IofRunnerData> runners, IReadOnlyList<RelayProtocolTeam>? relayTeams = null) =>
        new(new IofEventData("Test", "", 1),
        [
            new IofDayExport(race,
                new ResultProtocolData([new ResultProtocolGroup("Ч21", 0, 3.5m, 3, null, false, rows, RelayTeams: relayTeams)]),
                null,
                new Dictionary<string, IofClassInfo>(TextKey.Comparer) { ["Ч21"] = info },
                runners)
        ]);

    private static readonly IofClassInfo SetCourse = new(3.5m, 3, CompetitorModel.Individual, false, false);

    private static IofRaceResult Single(IofExportData data) =>
        Builder.BuildResultList(data, Options).Classes.Single().Persons.Single().Results.Single();

    private static void Valid(IofExportData data)
    {
        Checks.IsIofXml(Writer.Write(Builder.BuildResultList(data, Options)));
    }

    // ---- 1. statuses (§3.1)

    [Theory]
    [InlineData(FinishStatus.Ok, false, true, false, IofResultStatus.OK)]
    [InlineData(FinishStatus.Ok, true, true, false, IofResultStatus.NotCompeting)]
    [InlineData(FinishStatus.Mp, false, true, false, IofResultStatus.MissingPunch)]
    [InlineData(FinishStatus.Mp, true, true, false, IofResultStatus.MissingPunch)]
    [InlineData(FinishStatus.Ovt, false, true, false, IofResultStatus.OverTime)]
    [InlineData(FinishStatus.Dnf, false, true, false, IofResultStatus.DidNotFinish)]
    [InlineData(FinishStatus.Dns, false, false, false, IofResultStatus.DidNotStart)]
    [InlineData(FinishStatus.Dsq, false, true, false, IofResultStatus.Disqualified)]
    [InlineData(FinishStatus.None, false, true, false, IofResultStatus.Finished)]
    [InlineData(FinishStatus.None, false, false, false, IofResultStatus.Inactive)]
    [InlineData(FinishStatus.None, false, false, true, IofResultStatus.DidNotStart)]
    public void Status_FollowsTheTable(FinishStatus status, bool outOfCompetition, bool readout, bool locked, IofResultStatus expected)
    {
        var id = Guid.NewGuid();
        var start = readout ? At(10, 0) : (DateTimeOffset?)null;
        var finish = readout ? At(10, 30) : (DateTimeOffset?)null;
        var place = status == FinishStatus.Ok && !outOfCompetition ? 1 : (int?)null;
        var data = OneGroup(Race(locked), SetCourse,
            [Row("Іваненко Петро", Result(status, start, finish, place, readout, outOfCompetition), id)],
            new Dictionary<Guid, IofRunnerData> { [id] = Runner(start) });

        var result = Single(data);
        Assert.Equal(expected, result.Status);
        Assert.Equal(expected == IofResultStatus.OK ? 1 : null, result.Position);
        Valid(data);
    }

    [Fact]
    public void Status_AnOkResultWithheldFromRankingHasNoPosition()
    {
        var id = Guid.NewGuid();
        var result = Result(FinishStatus.Ok, At(10, 0), At(10, 30)) with { UnrankedReason = "plugin.reason" };
        var data = OneGroup(Race(), SetCourse, [Row("Іваненко Петро", result, id)],
            new Dictionary<Guid, IofRunnerData> { [id] = Runner(At(10, 0)) });

        var r = Single(data);
        Assert.Equal(IofResultStatus.OK, r.Status);
        Assert.Null(r.Position);
        Assert.Equal(1800, r.TimeSeconds);
    }

    // ---- 2. the time line (§3.2)

    [Fact]
    public void Time_PastMidnightFinishesOnTheNextDay()
    {
        var id = Guid.NewGuid();
        var data = OneGroup(Race(), SetCourse,
            [Row("Нічний Бігун", Result(FinishStatus.Ok, At(23, 50), At(0, 20, dayShift: 1), 1), id)],
            new Dictionary<Guid, IofRunnerData> { [id] = Runner(At(23, 50)) });

        var r = Single(data);
        Assert.Equal(new DateTimeOffset(2026, 7, 4, 23, 50, 0, Kyiv), r.Start);
        Assert.Equal(new DateTimeOffset(2026, 7, 5, 0, 20, 0, Kyiv), r.Finish);
        Assert.Equal(1800, r.TimeSeconds);
    }

    [Fact]
    public void Time_ASportTimeStampKeepsItsClockOnTheDayDate()
    {
        // Sport Time writes 2001-01-01 with a winter +02:00; the clock (10:00) is what the chip read.
        var winter = TimeSpan.FromHours(2);
        var start = new DateTimeOffset(2001, 1, 1, 10, 0, 0, winter);
        var finish = new DateTimeOffset(2001, 1, 1, 10, 41, 30, winter);
        var id = Guid.NewGuid();
        var data = OneGroup(Race(), SetCourse,
            [Row("Іваненко Петро", Result(FinishStatus.Ok, start, finish, 1), id)],
            new Dictionary<Guid, IofRunnerData> { [id] = Runner(start) });

        var r = Single(data);
        Assert.Equal(new DateTimeOffset(2026, 7, 4, 10, 0, 0, Kyiv), r.Start);
        Assert.Equal(new DateTimeOffset(2026, 7, 4, 10, 41, 30, Kyiv), r.Finish);
        Assert.Equal(2490, r.TimeSeconds);
    }

    [Fact]
    public void Time_WithoutAReadoutTheAssignedStartIsWritten()
    {
        var id = Guid.NewGuid();
        var data = OneGroup(Race(), SetCourse,
            [Row("Іваненко Петро", Result(FinishStatus.None, null, null, readout: false), id)],
            new Dictionary<Guid, IofRunnerData> { [id] = new("", "", new TimeSpan(11, 5, 0), null, null, null) });

        var r = Single(data);
        Assert.Equal(new DateTimeOffset(2026, 7, 4, 11, 5, 0, Kyiv), r.Start);
        Assert.Null(r.Finish);
        Assert.Null(r.TimeSeconds);
        Assert.Null(r.Chip);
    }

    // ---- 3. names (§3.3)

    [Theory]
    [InlineData("Іваненко Петро Іванович", "Іваненко", "Петро Іванович")]
    [InlineData("Мельник", "Мельник", "")]
    [InlineData("  Ткач   Лев ", "Ткач", "Лев")]
    public void Name_FirstWordIsTheFamily(string fullName, string family, string given)
    {
        var id = Guid.NewGuid();
        var data = OneGroup(Race(), SetCourse,
            [Row(fullName, Result(FinishStatus.Ok, At(10, 0), At(10, 30), 1), id)],
            new Dictionary<Guid, IofRunnerData> { [id] = Runner(At(10, 0)) });

        var person = Builder.BuildResultList(data, Options).Classes.Single().Persons.Single().Person;
        Assert.Equal(family, person.Family);
        Assert.Equal(given, person.Given);
        // The EntryList import joins Family + " " + Given back (blank parts dropped).
        Assert.Equal(string.Join(' ', new[] { family, given }.Where(s => s.Length > 0)),
            string.Join(' ', new[] { person.Family, person.Given }.Where(s => s.Length > 0)));
        Valid(data);
    }

    // ---- 4. splits (§3.5)

    private static PassagePunch Punch(string code, int minute, bool onCourse, int? courseIndex) =>
        new(0, code, onCourse, At(10, minute), null, TimeSpan.FromMinutes(minute), PassageKind.Control, CourseIndex: courseIndex);

    private static SplitsView Ordered(string[] course, params PassagePunch[] punches) => new()
    {
        Layout = SplitsLayout.Ordered,
        Passage = punches,
        Expected = course.Select((c, i) => new ExpectedControl(i + 1, c, punches.Any(p => p.CourseIndex == i))).ToList()
    };

    private static IReadOnlyList<IofSplit> SplitsOf(SplitsView view, bool usesScore = false)
    {
        var id = Guid.NewGuid();
        var info = SetCourse with { UsesScore = usesScore };
        var data = OneGroup(Race(), info,
            [Row("Іваненко Петро", Result(FinishStatus.Mp, At(10, 0), At(10, 40)), id)],
            new Dictionary<Guid, IofRunnerData> { [id] = Runner(At(10, 0), view) });
        Valid(data);
        return Single(data).Splits;
    }

    [Fact]
    public void Splits_AMissedControlIsMissingAndAnExtraPunchIsAdditionalAtTheEnd()
    {
        var splits = SplitsOf(Ordered(["31", "32", "33"],
            Punch("31", 5, true, 0), Punch("45", 7, false, null), Punch("33", 15, true, 2)));

        Assert.Equal(
            [("31", (int?)300, IofSplitStatus.OK), ("32", null, IofSplitStatus.Missing), ("33", 900, IofSplitStatus.OK),
             ("45", 420, IofSplitStatus.Additional)],
            splits.Select(s => (s.Code, s.TimeSeconds, s.Status)));
    }

    [Fact]
    public void Splits_ACourseVisitingAControlTwiceKeepsBothVisits()
    {
        var splits = SplitsOf(Ordered(["55", "44", "43", "55", "66"],
            Punch("55", 3, true, 0), Punch("44", 6, true, 1), Punch("43", 9, true, 2), Punch("55", 12, true, 3), Punch("66", 15, true, 4)));

        Assert.Equal(["55", "44", "43", "55", "66"], splits.Select(s => s.Code));
        Assert.Equal([180, 360, 540, 720, 900], splits.Select(s => s.TimeSeconds ?? -1));
        Assert.All(splits, s => Assert.Equal(IofSplitStatus.OK, s.Status));
    }

    [Fact]
    public void Splits_ADisabledControlIsLeftOutAndItsPunchIsAdditional()
    {
        var view = Ordered(["31", "33"], Punch("31", 5, true, 0), Punch("32", 8, false, null), Punch("33", 12, true, 1)) with
        {
            Expected =
            [
                new ExpectedControl(1, "31", true), new ExpectedControl(2, "33", true),
                new ExpectedControl(0, "32", false, Ignored: true)
            ]
        };
        var splits = SplitsOf(view);
        Assert.Equal(["31", "33", "32"], splits.Select(s => s.Code));
        Assert.Equal(IofSplitStatus.Additional, splits[2].Status);
    }

    [Fact]
    public void Splits_AScoredOrderedViewNeverHasMissingControls()
    {
        // Rogaine builds an ordered view over the whole catalogue; an untaken control is not a missing one.
        var view = Ordered(["31", "32", "33", "34"], Punch("33", 5, true, 2), Punch("31", 9, true, 0), Punch("31", 11, false, null));
        var splits = SplitsOf(view, usesScore: true);
        Assert.Equal(
            [("33", IofSplitStatus.OK), ("31", IofSplitStatus.OK), ("31", IofSplitStatus.Additional)],
            splits.Select(s => (s.Code, s.Status)));
    }

    [Fact]
    public void Splits_NoReadOutNoSplits()
    {
        var id = Guid.NewGuid();
        var data = OneGroup(Race(), SetCourse,
            [Row("Іваненко Петро", Result(FinishStatus.None, null, null, readout: false), id)],
            new Dictionary<Guid, IofRunnerData> { [id] = Runner(null) });
        Assert.Empty(Single(data).Splits);
    }

    // ---- 5. relay (§3.7)

    [Fact]
    public void Relay_OverallOnEveryLegPositionOnlyOnTheLastEmptyLegSkippedNamelessTeamKept()
    {
        var info = new IofClassInfo(3.5m, 3, CompetitorModel.TeamWithLegs, false, false);
        var a1 = Guid.NewGuid();
        var a2 = Guid.NewGuid();
        var b1 = Guid.NewGuid();
        RelayLegResult Leg(int leg, Guid id, DateTimeOffset start, DateTimeOffset finish, TimeSpan cumulative) =>
            new(leg, Guid.NewGuid(), id, start, finish, FinishStatus.Ok, finish - start, finish - start, cumulative, false, false);
        var teamA = new TeamDayResult(Guid.NewGuid(), 101, "Вітер", Guid.Empty, FinishStatus.Ok, FinishStatus.Ok, null,
            TimeSpan.FromMinutes(45), TimeSpan.FromMinutes(45),
            [Leg(1, a1, At(11, 0), At(11, 20), TimeSpan.FromMinutes(20)), Leg(2, a2, At(11, 20), At(11, 45), TimeSpan.FromMinutes(45))])
        {
            Place = 1
        };
        var teamB = new TeamDayResult(Guid.NewGuid(), null, "", Guid.Empty, FinishStatus.Ok, FinishStatus.Ok, null,
            TimeSpan.FromMinutes(50), TimeSpan.FromMinutes(50), [Leg(1, b1, At(11, 0), At(11, 25), TimeSpan.FromMinutes(25))])
        {
            Place = 2
        };
        ResultProtocolRow Runs(string name, Guid id, TeamDayResult team, int leg, string bib) =>
            Row(name, Result(FinishStatus.Ok, null, team.Legs[leg - 1].Finish, team.Place) with { Team = team, Leg = team.Legs[leg - 1] }, id, number: bib);
        var rowA1 = Runs("Гончар Іра", a1, teamA, 1, "101.1");
        var rowA2 = Runs("Гончар Оля", a2, teamA, 2, "101.2");
        var rowB1 = Runs("Литвин Ян", b1, teamB, 1, "");
        var data = OneGroup(Race(), info, [rowA1, rowA2, rowB1],
            new Dictionary<Guid, IofRunnerData> { [a1] = Runner(At(11, 0)), [a2] = Runner(At(11, 20)), [b1] = Runner(At(11, 0)) },
            [
                new RelayProtocolTeam(101, "Вітер", "", "", teamA, [new RelayProtocolLeg(1, rowA1), new RelayProtocolLeg(2, rowA2)]),
                new RelayProtocolTeam(null, "", "", "", teamB, [new RelayProtocolLeg(1, rowB1), new RelayProtocolLeg(2, null)]),
            ]);

        var cls = Builder.BuildResultList(data, Options).Classes.Single();
        Assert.Empty(cls.Persons);
        var a = cls.Teams[0];
        Assert.Equal(("Вітер", "101"), (a.Name, a.Bib));
        Assert.Equal([1, 2], a.Members.Select(m => m.Result.Leg ?? 0));
        Assert.All(a.Members, m => Assert.Equal(IofResultStatus.OK, m.Result.Overall!.Status));
        Assert.Null(a.Members[0].Result.Overall!.Position);
        Assert.Equal(1, a.Members[1].Result.Overall!.Position);
        Assert.Equal(0, a.Members[1].Result.Overall!.TimeBehindSeconds);
        Assert.Equal(2700, a.Members[1].Result.Overall!.TimeSeconds);
        Assert.Equal(1500, a.Members[1].Result.TimeSeconds);

        var b = cls.Teams[1];
        Assert.Equal(("", (string?)null), (b.Name, b.Bib)); // a team without a name or a number stays so
        Assert.Single(b.Members);                            // the leg nobody runs is left out
        Assert.Equal(2, b.Members[0].Result.Overall!.Position);
        Valid(data);
    }

    // ---- 6. the runner's course (§3.4)

    [Fact]
    public void Course_GroupAndCodeReadBackByTheCourseImport()
    {
        var id = Guid.NewGuid();
        var info = SetCourse with { DistanceKm = 4.25m, ControlCount = 3 };
        var data = OneGroup(Race(), info,
            [Row("Іваненко Петро", Result(FinishStatus.Ok, At(10, 0), At(10, 30), 1), id)],
            new Dictionary<Guid, IofRunnerData> { [id] = Runner(At(10, 0), course: new IofRunnerCourse("A", null, null, 3)) });

        var list = Builder.BuildResultList(data, Options);
        var classCourse = list.Classes.Single().Courses.Single();
        Assert.Equal(("Ч21", 4250, 3), (classCourse.Name, classCourse.LengthMetres ?? 0, classCourse.NumberOfControls ?? 0));
        var course = Single(data).Course!;
        Assert.Equal(("Ч21_A", "Ч21"), (course.Name, course.Family));

        // The course import splits it back into the same group and code.
        var parsed = Sp.GetRequiredService<IIofXmlParser>().Parse($"""
            <CourseData xmlns="http://www.orienteering.org/datastandard/3.0" iofVersion="3.0"><RaceCourseData>
              <Course><Name>{course.Name}</Name><CourseFamily>{course.Family}</CourseFamily>
                <CourseControl><Control>31</Control></CourseControl><CourseControl><Control>32</Control></CourseControl></Course>
              <Course><Name>Ч21_B</Name><CourseFamily>Ч21</CourseFamily>
                <CourseControl><Control>32</Control></CourseControl><CourseControl><Control>31</Control></CourseControl></Course>
            </RaceCourseData></CourseData>
            """);
        var group = parsed.Courses.Single();
        Assert.Equal("Ч21", group.Name);
        Assert.Contains(group.Variants, v => v.Code == "A");
        Valid(data);
    }

    [Fact]
    public void Course_TheNameTheCourseFileGaveWins()
    {
        var id = Guid.NewGuid();
        var data = OneGroup(Race(), SetCourse,
            [Row("Іваненко Петро", Result(FinishStatus.Ok, At(10, 0), At(10, 30), 1), id)],
            new Dictionary<Guid, IofRunnerData> { [id] = Runner(At(10, 0), course: new IofRunnerCourse("A", "ЧЖ21.1", "ЧЖ21", null)) });

        var course = Single(data).Course!;
        Assert.Equal(("ЧЖ21.1", "ЧЖ21"), (course.Name, course.Family));
    }

    [Fact]
    public void Writer_UsesTheIofNamespaceUtf8AndUnixLineEnds()
    {
        var id = Guid.NewGuid();
        var data = OneGroup(Race(), SetCourse,
            [Row("Іваненко Петро", Result(FinishStatus.Ok, At(10, 0), At(10, 30), 1), id)],
            new Dictionary<Guid, IofRunnerData> { [id] = Runner(At(10, 0)) });
        var bytes = Writer.Write(Builder.BuildResultList(data, Options));
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<ResultList ", text);
        Assert.Contains("xmlns=\"http://www.orienteering.org/datastandard/3.0\"", text);
        Assert.Contains("iofVersion=\"3.0\"", text);
        Assert.DoesNotContain("\r", text);
        Assert.Contains("<Family>Іваненко</Family>", text);
    }
}
