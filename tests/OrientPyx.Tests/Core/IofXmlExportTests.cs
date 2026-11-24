using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Tests.Support;
using Xunit.Abstractions;

namespace OrientPyx.Tests.Core;

/// <summary>
/// «Експорт IOF XML» end to end, through the real editor (docs/iof-xml-export-plan.md §7.2): a day with a set course
/// (two equal times, MP, DNF, a DSQ override, «П/К», no read-out, an FSOU code), scatter, a rogaine patrol and a relay
/// with a mass start; the ResultList / StartList equal their approved files, validate against the official IOF.xsd and
/// agree with the result protocol; a multi-day file carries races and the running total; the options and the page's
/// saved settings work.
/// </summary>
[Collection(AppCollection.Name)]
public sealed class IofXmlExportTests
{
    private static readonly XNamespace Ns = IofDataStandard.Namespace;
    private static readonly DateTimeOffset CreateTime = new(2026, 6, 6, 20, 0, 0, CompetitionBuilder.Offset);

    private readonly ITestOutputHelper _output;

    public IofXmlExportTests(ITestOutputHelper output) => _output = output;

    private static IServiceProvider Sp => TestEnvironment.Instance.Services;
    private static ICompetitionEditorService Editor => Sp.GetRequiredService<ICompetitionEditorService>();
    private static IIofExportBuilder Builder => Sp.GetRequiredService<IIofExportBuilder>();
    private static IIofXmlWriter Writer => Sp.GetRequiredService<IIofXmlWriter>();
    private static TimeSpan T(int h, int m, int s = 0) => new(h, m, s);

    private static IofExportOptions Options(
        IofOrganisationSource organisation = IofOrganisationSource.Club, IReadOnlySet<string>? groups = null, bool birthDates = true) =>
        new(groups, organisation, birthDates, "Кубок тесту", "OrientPyx test", CreateTime);

    // One competition with every format the export distinguishes; day 2 (when asked) repeats the set course.
    private static async Task<CompetitionBuilder> BuildAsync(int dayCount = 1)
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("iof"), dayCount, name: "Кубок тесту");
        var store = Sp.GetRequiredService<IEventStore>();
        var club = new Club { Name = "Азимут" };
        await store.AddClubAsync(c.Folder, club);
        var region = new Region { Name = "Київська" };
        await store.AddRegionAsync(c.Folder, region);

        for (var day = 1; day <= dayCount; day++)
        {
            var cps = await c.AddControlsAsync(day, "S1", "31", "32", "33", "34", "F1");
            foreach (var cp in cps.Where(p => p.Code.StartsWith('3')))
            {
                cp.Points = 3;
                await store.UpdateControlPointAsync(c.Folder, cp);
            }
        }

        await c.AddGroupAsync("Ч21", "S1 31 32 33 F1");
        await c.AddGroupAsync("Ж21", "S1 31 32 33 F1", s => s.DisciplineOverride = DisciplineId.Scatter);
        for (var day = 1; day <= dayCount; day++)
            await c.SetCourseVariantsAsync("Ж21", day, ("A", "S1 31 32 33 F1"), ("B", "S1 33 32 31 F1"));
        await c.AddGroupAsync("Рогейн", "31 32 33 34", s => { s.DisciplineOverride = DisciplineId.Rogaine; s.TimeLimitSeconds = 3600; });
        await c.AddGroupAsync("Естафета", "S1 31 32 33 F1", s =>
        {
            s.DisciplineOverride = DisciplineId.Relay;
            s.LegCount = 2;
            s.MassStartTime = T(11, 0);
        });

        // Set course: two equal times, MP, DNF, a DSQ override, «П/К», no read-out.
        await c.AddRunnerAsync("Іваненко Петро", "1", "101", "Ч21", T(10, 0), p => { p.FsouCode = "12345"; p.ClubId = club.Id; p.RegionId = region.Id; });
        await c.AddRunnerAsync("Петренко Олег", "2", "102", "Ч21", T(10, 5), p => p.RegionId = region.Id);
        await c.AddRunnerAsync("Сидоренко Іван", "3", "103", "Ч21", T(10, 10));
        await c.AddRunnerAsync("Коваль Андрій", "4", "104", "Ч21", T(10, 15), configureDay: d => d.ResultStatusOverride = FinishStatus.Dnf);
        await c.AddRunnerAsync("Шевчук Степан", "5", "105", "Ч21", T(10, 20), configureDay: d => d.ResultStatusOverride = FinishStatus.Dsq);
        await c.AddRunnerAsync("Гість Ігор", "6", "106", "Ч21", T(10, 25), configureDay: d => d.OutOfCompetition = true);
        await c.AddRunnerAsync("Без Зчитки", "7", "107", "Ч21", T(10, 30));
        await c.AddRunnerAsync("Мороз Ганна", "21", "201", "Ж21", T(10, 0));
        await c.AddRunnerAsync("Бондар Ольга", "22", "202", "Ж21", T(10, 1));
        await c.AddRunnerAsync("Ткач Марко", "31", "301", "Рогейн", T(10, 0), p => p.Team = "Сокіл");
        await c.AddRunnerAsync("Ткач Лев", "32", "302", "Рогейн", T(10, 0), p => p.Team = "Сокіл");
        await c.AddRunnerAsync("Олійник Ліна", "33", "303", "Рогейн", T(10, 0));
        foreach (var (name, chip) in new[] { ("Гончар Іра", "401"), ("Гончар Оля", "402"), ("Литвин Ян", "411"), ("Литвин Ада", "412") })
            await c.AddRunnerAsync(name, "", chip, "Естафета", days: [1]);
        await c.AddRelayTeamAsync("Естафета", 101, "Вітер", 1, ("Гончар Іра", 1), ("Гончар Оля", 2));
        await c.AddRelayTeamAsync("Естафета", 102, "Хвиля", 1, ("Литвин Ян", 1), ("Литвин Ада", 2));

        for (var day = 1; day <= dayCount; day++)
        {
            var shift = day - 1; // day 2: a minute slower each
            await c.AddReadoutAsync(day, "101", "31 32 33", T(10, 0), T(10, 30 + shift));
            await c.AddReadoutAsync(day, "102", "31 32 33", T(10, 5), T(10, 35 + 2 * shift));
            await c.AddReadoutAsync(day, "103", "31 33", T(10, 10), T(10, 38));
            await c.AddReadoutAsync(day, "104", "31", T(10, 15), T(10, 30));
            await c.AddReadoutAsync(day, "105", "31 32 33", T(10, 20), T(10, 45));
            await c.AddReadoutAsync(day, "106", "31 32 33", T(10, 25), T(10, 50));
            await c.AddReadoutAsync(day, "201", "31 32 33", T(10, 0), T(10, 25));
            await c.AddReadoutAsync(day, "202", "33 32 31", T(10, 1), T(10, 25));
            await c.AddReadoutAsync(day, "301", "31 32 33 34", T(10, 0), T(10, 50));
            await c.AddReadoutAsync(day, "302", "34 33 32", T(10, 0), T(10, 52));
            await c.AddReadoutAsync(day, "303", "31 32", T(10, 0), T(10, 40));
        }
        // Relay chips without a start box (leg 1 runs from the mass start, leg 2 from the hand-over); the chip start
        // given here only spreads the punches over the leg.
        await AddLegReadoutAsync(c, "401", "31 32 33", T(11, 0), T(11, 20));
        await AddLegReadoutAsync(c, "402", "31 32 33", T(11, 20), T(11, 45));
        await AddLegReadoutAsync(c, "411", "31 32 33", T(11, 0), T(11, 22));
        await AddLegReadoutAsync(c, "412", "31 33", T(11, 22), T(11, 50));
        await c.SelectAsync();
        return c;
    }

    // A read-out of day 1 with no start box, its punches spread from legStart to the finish.
    private static async Task AddLegReadoutAsync(CompetitionBuilder c, string chip, string punches, TimeSpan legStart, TimeSpan finish)
    {
        var start = c.Date + legStart;
        var codes = punches.Split(' ');
        var leg = (c.Date + finish - start) / (codes.Length + 1);
        await Sp.GetRequiredService<IEventStore>().AddFinishReadoutsAsync(c.Folder,
        [
            new FinishReadout
            {
                EventDayId = c.Day(1).Id, Order = 1000 + int.Parse(chip), ChipNumber = chip, StartTime = null,
                FinishTime = c.Date + finish, Punches = punches,
                PunchTimes = string.Join(',', codes.Select((code, k) => $"{code}@{(start + leg * (k + 1)).UtcTicks}")),
                ContentKey = $"{c.Summary.Identifier}-{chip}-leg", CreatedAt = c.Date + finish
            }
        ]);
    }

    private static async Task<byte[]> ResultListAsync(CompetitionBuilder c, IofExportOptions? options = null, params int[] days)
    {
        var ids = (days.Length == 0 ? [1] : days).Select(n => c.Day(n).Id).ToList();
        var data = await Editor.GetIofExportDataAsync(ids, IofListKind.ResultList);
        return Writer.Write(Builder.BuildResultList(data, options ?? Options()));
    }

    private static async Task<byte[]> StartListAsync(CompetitionBuilder c, IofExportOptions? options = null, params int[] days)
    {
        var ids = (days.Length == 0 ? [1] : days).Select(n => c.Day(n).Id).ToList();
        var data = await Editor.GetIofExportDataAsync(ids, IofListKind.StartList);
        return Writer.Write(Builder.BuildStartList(data, options ?? Options()));
    }

    // The approved file in the source tree; a missing one is written there (approve it by keeping it), a mismatch
    // writes <name>.actual.xml into the run's results folder.
    private void AssertGolden(string name, byte[] actual)
    {
        var golden = Path.Combine(TestEnvironment.RepoRoot, "tests", "OrientPyx.Tests", "Golden", name);
        if (!File.Exists(golden))
        {
            File.WriteAllBytes(golden, actual);
            _output.WriteLine($"no approved {name} yet — written to {golden}");
            return;
        }
        // The writer's "\n" is the truth; a checkout that turned the approved file into CRLF still compares equal.
        var expected = File.ReadAllText(golden, Encoding.UTF8).Replace("\r\n", "\n");
        if (expected == Encoding.UTF8.GetString(actual))
            return;
        Directory.CreateDirectory(TestEnvironment.ResultsDir);
        var actualPath = Path.Combine(TestEnvironment.ResultsDir, Path.ChangeExtension(name, ".actual.xml"));
        File.WriteAllBytes(actualPath, actual);
        Assert.Fail($"{name} differs from {golden} — actual written to {actualPath}");
    }

    private static XDocument Parse(byte[] bytes) => XDocument.Parse(Encoding.UTF8.GetString(bytes));

    private static XElement PersonResult(XDocument doc, string family, string given) =>
        doc.Descendants(Ns + "PersonResult").Single(p =>
            (string?)p.Element(Ns + "Person")!.Element(Ns + "Name")!.Element(Ns + "Family") == family
            && (string?)p.Element(Ns + "Person")!.Element(Ns + "Name")!.Element(Ns + "Given") == given);

    // ---- single day

    [Fact]
    public async Task ResultList_MatchesTheApprovedFileAndTheSchema()
    {
        var c = await BuildAsync();
        var bytes = await ResultListAsync(c);
        Checks.IsIofXml(bytes);
        AssertGolden("iof-resultlist.xml", bytes);
    }

    [Fact]
    public async Task StartList_MatchesTheApprovedFileAndTheSchema()
    {
        var c = await BuildAsync();
        var bytes = await StartListAsync(c);
        Checks.IsIofXml(bytes);
        AssertGolden("iof-startlist.xml", bytes);
    }

    [Fact]
    public async Task ResultList_StatusesTimesAndSplitsFollowTheProtocol()
    {
        var c = await BuildAsync();
        var doc = Parse(await ResultListAsync(c));

        Assert.Equal("Snapshot", (string?)doc.Root!.Attribute("status")); // the day is still open
        var ivanenko = PersonResult(doc, "Іваненко", "Петро").Element(Ns + "Result")!;
        Assert.Equal("OK", (string?)ivanenko.Element(Ns + "Status"));
        Assert.Equal("1", (string?)ivanenko.Element(Ns + "Position"));
        Assert.Equal("1800", (string?)ivanenko.Element(Ns + "Time"));
        Assert.Equal("2026-06-06T10:00:00+03:00", (string?)ivanenko.Element(Ns + "StartTime"));
        Assert.Equal("2026-06-06T10:30:00+03:00", (string?)ivanenko.Element(Ns + "FinishTime"));
        Assert.Equal("101", (string?)ivanenko.Element(Ns + "ControlCard"));
        Assert.Equal("12345", (string?)PersonResult(doc, "Іваненко", "Петро").Element(Ns + "Person")!.Element(Ns + "Id"));
        Assert.Equal(["31", "32", "33"], ivanenko.Elements(Ns + "SplitTime").Select(s => (string)s.Element(Ns + "ControlCode")!));

        var petrenko = PersonResult(doc, "Петренко", "Олег").Element(Ns + "Result")!;
        Assert.Equal("1", (string?)petrenko.Element(Ns + "Position")); // the same time shares the place
        Assert.Equal("0", (string?)petrenko.Element(Ns + "TimeBehind"));

        var mp = PersonResult(doc, "Сидоренко", "Іван").Element(Ns + "Result")!;
        Assert.Equal("MissingPunch", (string?)mp.Element(Ns + "Status"));
        Assert.Null(mp.Element(Ns + "Position"));
        Assert.Equal("1680", (string?)mp.Element(Ns + "Time")); // finish − start, as WinSplits shows an MP
        var missing = mp.Elements(Ns + "SplitTime").Single(s => (string?)s.Attribute("status") == "Missing");
        Assert.Equal("32", (string?)missing.Element(Ns + "ControlCode"));
        Assert.Null(missing.Element(Ns + "Time"));

        Assert.Equal("DidNotFinish", (string?)PersonResult(doc, "Коваль", "Андрій").Element(Ns + "Result")!.Element(Ns + "Status"));
        Assert.Equal("Disqualified", (string?)PersonResult(doc, "Шевчук", "Степан").Element(Ns + "Result")!.Element(Ns + "Status"));
        var guest = PersonResult(doc, "Гість", "Ігор").Element(Ns + "Result")!;
        Assert.Equal("NotCompeting", (string?)guest.Element(Ns + "Status"));
        Assert.Equal("1500", (string?)guest.Element(Ns + "Time"));
        var unread = PersonResult(doc, "Без", "Зчитки").Element(Ns + "Result")!;
        Assert.Equal("Inactive", (string?)unread.Element(Ns + "Status")); // the day is open
        Assert.Equal("2026-06-06T10:30:00+03:00", (string?)unread.Element(Ns + "StartTime"));
        Assert.Empty(unread.Elements(Ns + "SplitTime"));
    }

    [Fact]
    public async Task ResultList_AClosedDayIsCompleteAndTheUnreadDidNotStart()
    {
        var c = await BuildAsync();
        var store = Sp.GetRequiredService<IEventStore>();
        var day = c.Day(1);
        day.IsLocked = true;
        await store.UpdateDayAsync(c.Folder, day);

        var doc = Parse(await ResultListAsync(c));
        Assert.Equal("Complete", (string?)doc.Root!.Attribute("status"));
        Assert.Equal("DidNotStart", (string?)PersonResult(doc, "Без", "Зчитки").Element(Ns + "Result")!.Element(Ns + "Status"));
    }

    [Fact]
    public async Task ResultList_ScatterRogaineAndRelayTakeTheirIofShape()
    {
        var c = await BuildAsync();
        var doc = Parse(await ResultListAsync(c));

        // Scatter: the runner's own course «група_код» in the group's family.
        var course = PersonResult(doc, "Бондар", "Ольга").Element(Ns + "Result")!.Element(Ns + "Course")!;
        Assert.Equal("Ж21_B", (string?)course.Element(Ns + "Name"));
        Assert.Equal("Ж21", (string?)course.Element(Ns + "CourseFamily"));

        // Rogaine: one patrol, both on leg 1, the team score in OverallResult, no Missing splits.
        var patrol = doc.Descendants(Ns + "TeamResult").Single(t => (string?)t.Element(Ns + "Name") == "Сокіл");
        var members = patrol.Elements(Ns + "TeamMemberResult").ToList();
        Assert.Equal(2, members.Count);
        Assert.All(members, m => Assert.Equal("1", (string?)m.Element(Ns + "Result")!.Element(Ns + "Leg")));
        var overall = members[0].Element(Ns + "Result")!.Element(Ns + "OverallResult")!;
        Assert.Equal("OK", (string?)overall.Element(Ns + "Status"));
        Assert.Equal("1", (string?)overall.Element(Ns + "Position"));
        Assert.NotNull(overall.Elements(Ns + "Score").SingleOrDefault(s => (string?)s.Attribute("type") == "Score"));
        Assert.DoesNotContain(patrol.Descendants(Ns + "SplitTime"), s => (string?)s.Attribute("status") == "Missing");
        Assert.NotNull(PersonResult(doc, "Олійник", "Ліна")); // teamless — a person

        // Relay: a team per relay team, a member per leg, the team's place only on the last leg.
        var team = doc.Descendants(Ns + "TeamResult").Single(t => (string?)t.Element(Ns + "Name") == "Вітер");
        Assert.Equal("101", (string?)team.Element(Ns + "BibNumber"));
        var legs = team.Elements(Ns + "TeamMemberResult").Select(m => m.Element(Ns + "Result")!).ToList();
        Assert.Equal(["1", "2"], legs.Select(l => (string)l.Element(Ns + "Leg")!));
        Assert.Equal(["101.1", "101.2"], legs.Select(l => (string)l.Element(Ns + "BibNumber")!));
        Assert.Equal("2026-06-06T11:00:00+03:00", (string?)legs[0].Element(Ns + "StartTime")); // the mass start
        Assert.Equal("2026-06-06T11:20:00+03:00", (string?)legs[1].Element(Ns + "StartTime")); // the hand-over
        Assert.Null(legs[0].Element(Ns + "OverallResult")!.Element(Ns + "Position"));
        Assert.Equal("1", (string?)legs[1].Element(Ns + "OverallResult")!.Element(Ns + "Position"));
        Assert.Equal("2700", (string?)legs[1].Element(Ns + "OverallResult")!.Element(Ns + "Time"));
    }

    [Fact]
    public async Task ResultList_PlacesAndStatusesAgreeWithTheResultProtocol()
    {
        var c = await BuildAsync();
        var protocol = await c.ProtocolDataAsync();
        var doc = Parse(await ResultListAsync(c));

        foreach (var row in protocol.Groups.Where(g => g.Name is "Ч21" or "Ж21").SelectMany(g => g.Rows))
        {
            var space = row.FullName.IndexOf(' ');
            var result = PersonResult(doc, row.FullName[..space], row.FullName[(space + 1)..]).Element(Ns + "Result")!;
            Assert.Equal(row.Result.Place?.ToString(), (string?)result.Element(Ns + "Position"));
            Assert.Equal(row.Result.Status == FinishStatus.Ok, (string?)result.Element(Ns + "Status") is "OK" or "NotCompeting");
        }
    }

    [Fact]
    public async Task Options_FilterGroupsAndPickTheOrganisation()
    {
        var c = await BuildAsync();
        var onlyMen = Parse(await ResultListAsync(c, Options(groups: new HashSet<string> { "Ч21" })));
        Assert.Equal(["Ч21"], onlyMen.Descendants(Ns + "ClassResult").Select(cr => (string)cr.Element(Ns + "Class")!.Element(Ns + "Name")!));

        var byClub = Parse(await ResultListAsync(c));
        var clubOrg = PersonResult(byClub, "Іваненко", "Петро").Element(Ns + "Organisation")!;
        Assert.Equal(("Азимут", "Club"), ((string)clubOrg.Element(Ns + "Name")!, (string)clubOrg.Attribute("type")!));
        // No club → the region.
        var fallback = PersonResult(byClub, "Петренко", "Олег").Element(Ns + "Organisation")!;
        Assert.Equal(("Київська", "NationalRegion"), ((string)fallback.Element(Ns + "Name")!, (string)fallback.Attribute("type")!));

        var byRegion = Parse(await ResultListAsync(c, Options(IofOrganisationSource.Region)));
        var regionOrg = PersonResult(byRegion, "Іваненко", "Петро").Element(Ns + "Organisation")!;
        Assert.Equal("Київська", (string?)regionOrg.Element(Ns + "Name"));

        var noBirth = Parse(await ResultListAsync(c, Options(birthDates: false)));
        Assert.Empty(noBirth.Descendants(Ns + "BirthDate"));
        Assert.NotEmpty(byClub.Descendants(Ns + "BirthDate"));
    }

    [Fact]
    public async Task StartList_RelayTeamsStartOnLegOneAndPatrolsOnLegOne()
    {
        var c = await BuildAsync();
        var doc = Parse(await StartListAsync(c));
        var team = doc.Descendants(Ns + "TeamStart").Single(t => (string?)t.Element(Ns + "Name") == "Вітер");
        var starts = team.Elements(Ns + "TeamMemberStart").Select(m => m.Element(Ns + "Start")!).ToList();
        Assert.Equal("2026-06-06T11:00:00+03:00", (string?)starts[0].Element(Ns + "StartTime"));
        Assert.Null(starts[1].Element(Ns + "StartTime"));
        var person = doc.Descendants(Ns + "PersonStart").First(p =>
            (string?)p.Element(Ns + "Person")!.Element(Ns + "Name")!.Element(Ns + "Family") == "Іваненко");
        Assert.Equal("2026-06-06T10:00:00+03:00", (string?)person.Element(Ns + "Start")!.Element(Ns + "StartTime"));
    }

    [Fact]
    public async Task UnknownDiscipline_StillWritesAValidFileWithoutPlaces()
    {
        var c = await BuildAsync();
        await c.UpdateGroupSettingsAsync("Ч21", 1, s => s.DisciplineOverride = new DisciplineId("missing.plugin"));
        var bytes = await ResultListAsync(c);
        Checks.IsIofXml(bytes);
        var doc = Parse(bytes);
        var men = doc.Descendants(Ns + "ClassResult").Single(cr => (string?)cr.Element(Ns + "Class")!.Element(Ns + "Name") == "Ч21");
        Assert.Empty(men.Descendants(Ns + "Position"));
        Assert.NotEmpty(men.Elements(Ns + "PersonResult"));
    }

    // ---- several days

    [Fact]
    public async Task MultiDay_RacesRaceNumbersAndTheRunningTotal()
    {
        var c = await BuildAsync(dayCount: 2);
        await c.SelectAsync();
        await Editor.SaveSummaryProtocolSettingsAsync(new SummaryProtocolSettings { Mode = SummaryMode.ByTime });
        var store = Sp.GetRequiredService<IEventStore>();
        var day2 = c.Day(2);
        day2.IsLocked = true;
        await store.UpdateDayAsync(c.Folder, day2);

        var bytes = await ResultListAsync(c, null, 1, 2);
        Checks.IsIofXml(bytes);
        AssertGolden("iof-resultlist-2days.xml", bytes);
        var doc = Parse(bytes);

        Assert.Equal("Snapshot", (string?)doc.Root!.Attribute("status")); // day 1 is still open
        var races = doc.Root.Element(Ns + "Event")!.Elements(Ns + "Race").ToList();
        Assert.Equal(["1", "2"], races.Select(r => (string)r.Element(Ns + "RaceNumber")!));
        Assert.Equal("2026-06-07", (string?)races[1].Element(Ns + "StartTime")!.Element(Ns + "Date"));

        var results = PersonResult(doc, "Іваненко", "Петро").Elements(Ns + "Result").ToList();
        Assert.Equal(["1", "2"], results.Select(r => (string)r.Attribute("raceNumber")!));
        Assert.Equal("2026-06-07T10:00:00+03:00", (string?)results[1].Element(Ns + "StartTime"));
        // Running total by time: 30:00 after day 1, 30:00 + 31:00 after day 2, both leading.
        Assert.Equal("1800", (string?)results[0].Element(Ns + "OverallResult")!.Element(Ns + "Time"));
        Assert.Equal("3660", (string?)results[1].Element(Ns + "OverallResult")!.Element(Ns + "Time"));
        Assert.Equal("1", (string?)results[1].Element(Ns + "OverallResult")!.Element(Ns + "Position"));
        var petrenko = PersonResult(doc, "Петренко", "Олег").Elements(Ns + "Result").ToList();
        Assert.Equal("1", (string?)petrenko[0].Element(Ns + "OverallResult")!.Element(Ns + "Position")); // tied after day 1
        Assert.Equal("2", (string?)petrenko[1].Element(Ns + "OverallResult")!.Element(Ns + "Position"));
        Assert.Equal("60", (string?)petrenko[1].Element(Ns + "OverallResult")!.Element(Ns + "TimeBehind"));
        // An MP on a counted day: no place in the total — no OverallResult.
        Assert.Null(PersonResult(doc, "Сидоренко", "Іван").Element(Ns + "Result")!.Element(Ns + "OverallResult"));
        // A team keeps its team result in OverallResult.
        var patrol = doc.Descendants(Ns + "TeamResult").First(t => (string?)t.Element(Ns + "Name") == "Сокіл");
        Assert.Equal("1", (string?)patrol.Element(Ns + "TeamMemberResult")!.Element(Ns + "Result")!.Attribute("raceNumber"));
    }

    [Fact]
    public async Task MultiDay_ADayTheSummaryDoesNotCountHasNoTotal()
    {
        var c = await BuildAsync(dayCount: 2);
        await c.SelectAsync();
        await Editor.SaveSummaryProtocolSettingsAsync(new SummaryProtocolSettings
        {
            Mode = SummaryMode.ByTime,
            Days =
            [
                new SummaryDaySetting { DayId = c.Day(1).Id, DayNumber = 1, Counted = false },
                new SummaryDaySetting { DayId = c.Day(2).Id, DayNumber = 2, Counted = true },
            ]
        });

        var doc = Parse(await ResultListAsync(c, null, 1, 2));
        var results = PersonResult(doc, "Іваненко", "Петро").Elements(Ns + "Result").ToList();
        Assert.Null(results[0].Element(Ns + "OverallResult"));
        Assert.Equal("1860", (string?)results[1].Element(Ns + "OverallResult")!.Element(Ns + "Time"));
    }

    [Fact]
    public async Task MultiDay_StartListHasAStartPerDay()
    {
        var c = await BuildAsync(dayCount: 2);
        var bytes = await StartListAsync(c, null, 1, 2);
        Checks.IsIofXml(bytes);
        var doc = Parse(bytes);
        var person = doc.Descendants(Ns + "PersonStart").First(p =>
            (string?)p.Element(Ns + "Person")!.Element(Ns + "Name")!.Element(Ns + "Family") == "Іваненко");
        Assert.Equal(["1", "2"], person.Elements(Ns + "Start").Select(s => (string)s.Attribute("raceNumber")!));
    }

    // ---- the page's saved options

    [Fact]
    public async Task Settings_AreSavedPerCompetition()
    {
        var c = await BuildAsync(dayCount: 2);
        await c.SelectAsync();
        Assert.Null(await Editor.GetIofExportSettingsAsync());

        await Editor.SaveIofExportSettingsAsync(new IofExportSettings
        {
            Organisation = IofOrganisationSource.Region,
            IncludeBirthDates = false,
            MultiDay = true,
            DayIds = [c.Day(2).Id]
        });
        var loaded = await Editor.GetIofExportSettingsAsync();
        Assert.NotNull(loaded);
        Assert.Equal(IofOrganisationSource.Region, loaded.Organisation);
        Assert.False(loaded.IncludeBirthDates);
        Assert.True(loaded.MultiDay);
        Assert.Equal([c.Day(2).Id], loaded.DayIds);
    }

    // ---- the course name the course file gave (plan §3.4)

    [Fact]
    public async Task CourseImport_KeepsTheFileCourseNameAndTheExportWritesIt()
    {
        var c = await CompetitionBuilder.CreateAsync(Sp, TestIds.Next("iofname"), discipline: DisciplineId.Scatter);
        await c.SelectAsync();
        var parser = Sp.GetRequiredService<IIofXmlParser>();
        var data = parser.Parse(CourseData3(
            ("ЧЖ21_A", "ЧЖ21", "31 32 33"),
            ("ЧЖ21_B", "ЧЖ21", "33 32 31")));
        // «Розділення груп»: the family «ЧЖ21» becomes the group «Ч21» (the name the export can no longer rebuild).
        var split = new IofCourseData
        {
            Version = data.Version,
            Controls = data.Controls,
            Courses = [CopyAs(data.Courses.Single(), "Ч21")]
        };
        await Editor.ImportControlPointsAsync(split, replaceAll: true);
        await Editor.ImportGroupsAsync(split, updateExisting: true);

        var store = Sp.GetRequiredService<IEventStore>();
        var variants = await store.GetCourseVariantsAsync(c.Folder, c.Day(1).Id);
        Assert.Equal(["ЧЖ21_A", "ЧЖ21_B"], variants.OrderBy(v => v.Order).Select(v => v.SourceCourseName));
        Assert.All(variants, v => Assert.Equal("ЧЖ21", v.SourceCourseFamily));

        // A hand edit of the variants keeps the names by code.
        var group = (await Editor.GetGroupDayRowsAsync()).Single(g => g.Name == "Ч21");
        await Editor.SaveCourseVariantsAsync(group.GroupId, [new CourseVariantRow("A", "31 32 33", null), new CourseVariantRow("B", "33 32 31", null)]);
        variants = await store.GetCourseVariantsAsync(c.Folder, c.Day(1).Id);
        Assert.Equal("ЧЖ21_B", variants.Single(v => v.Code == "B").SourceCourseName);

        var participant = new Participant { FullName = "Тест Бігун", Number = "1" };
        await store.AddParticipantAsync(c.Folder, participant);
        await store.AddParticipantDayAsync(c.Folder, new ParticipantDay
        {
            EventDayId = c.Day(1).Id, ParticipantId = participant.Id, GroupId = group.GroupId, Chip = "555", StartTime = T(10, 0)
        });
        await store.AddFinishReadoutsAsync(c.Folder,
        [
            new FinishReadout
            {
                EventDayId = c.Day(1).Id, Order = 0, ChipNumber = "555",
                StartTime = c.Date + T(10, 0), FinishTime = c.Date + T(10, 20), Punches = "33 32 31",
                PunchTimes = string.Join(',', new[] { "33", "32", "31" }.Select((code, i) => $"{code}@{(c.Date + T(10, 5 * (i + 1))).UtcTicks}")),
                ContentKey = $"{c.Summary.Identifier}-555", CreatedAt = c.Date + T(10, 20)
            }
        ]);

        var doc = Parse(await ResultListAsync(c));
        var course = doc.Descendants(Ns + "PersonResult").Single().Element(Ns + "Result")!.Element(Ns + "Course")!;
        Assert.Equal("ЧЖ21_B", (string?)course.Element(Ns + "Name"));
        Assert.Equal("ЧЖ21", (string?)course.Element(Ns + "CourseFamily"));
    }

    private static IofCourse CopyAs(IofCourse course, string name) => new()
    {
        Name = name,
        Length = course.Length,
        Climb = course.Climb,
        ControlCodes = course.ControlCodes,
        Variants = course.Variants,
        AllVariants = course.AllVariants,
        TeamCombinations = course.TeamCombinations,
        VariationUse = course.VariationUse,
        Pattern = course.Pattern
    };

    // A minimal IOF 3.0 CourseData: controls 31–33 plus S1/F1 and the given courses (name, family, codes).
    private static string CourseData3(params (string Name, string Family, string Codes)[] courses)
    {
        var controls = string.Concat(new[] { "S1", "31", "32", "33", "F1" }.Select(code => $"<Control><Id>{code}</Id></Control>"));
        var body = string.Concat(courses.Select(course =>
            $"<Course><Name>{course.Name}</Name><CourseFamily>{course.Family}</CourseFamily><Length>2500.0</Length>"
            + "<CourseControl type=\"Start\"><Control>S1</Control></CourseControl>"
            + string.Concat(course.Codes.Split(' ').Select(code => $"<CourseControl type=\"Control\"><Control>{code}</Control></CourseControl>"))
            + "<CourseControl type=\"Finish\"><Control>F1</Control></CourseControl></Course>"));
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <CourseData xmlns="http://www.orienteering.org/datastandard/3.0" iofVersion="3.0">
              <Event><Name>Test</Name></Event>
              <RaceCourseData>{controls}{body}</RaceCourseData>
            </CourseData>
            """;
    }
}
