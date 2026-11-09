using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.Tests.Support;
using static OrientPyx.Tests.DocShots.DemoBuilder;

namespace OrientPyx.Tests.DocShots;

/// <summary>
/// The competitions the documentation shows and offers for download (<c>website/content/demo/*.opyx</c>): a two-day
/// city cup on set courses for the general pages, and one small competition per format for the guides. Built once
/// per run into the test data root, before the screenshots open them.
/// </summary>
internal static class DemoCompetitions
{
    public const string Cup = "demo-kubok";
    public const string ScoreCount = "demo-vybir-kp";
    public const string ScoreTime = "demo-vybir-chas";
    public const string Rogaine = "demo-rogain";
    public const string Mixed = "demo-zmishanyi";
    public const string Scatter = "demo-rozsiyuvannia";
    public const string Relay = "demo-estafeta";

    public static IReadOnlyList<string> All { get; } = [Cup, ScoreCount, ScoreTime, Rogaine, Mixed, Scatter, Relay];

    private static readonly Lazy<Task> Built = new(BuildAllAsync, LazyThreadSafetyMode.ExecutionAndPublication);

    private static IServiceProvider Core => TestEnvironment.Instance.Services;

    public static Task EnsureAsync() => Built.Value;

    private static async Task BuildAllAsync()
    {
        await BuildDemosAsync();

        // The downloadable archives, exported before any screenshot touches the competitions.
        var archive = Core.GetRequiredService<IEventArchiveService>();
        Directory.CreateDirectory(DocShotOutput.DemoDir);
        foreach (var id in All)
            await archive.ExportAsync(id, Path.Combine(DocShotOutput.DemoDir, $"{id}.opyx"));
    }

    private static async Task BuildDemosAsync()
    {
        await BuildCupAsync();
        await BuildScoreCountAsync();
        await BuildScoreTimeAsync();
        await BuildRogaineAsync();
        await BuildMixedAsync();
        await BuildScatterAsync();
        await BuildRelayAsync();
    }

    private static readonly string[] Officials = ["Довженко Тарас", "Гнатюк Ірина", "Остапчук Олена", "Шаповал Олег"];

    private static Task OfficialsAsync(DemoBuilder b, string organisation) => b.UpdateInfoAsync(info =>
    {
        info.Organisation = organisation;
        info.CourseSetter = Officials[0];
        info.CourseSetterCategory = "НК";
        info.ChiefJudge = Officials[1];
        info.ChiefJudgeCategory = "НК";
        info.ChiefSecretary = Officials[2];
        info.ChiefSecretaryCategory = "I кат.";
    });

    // ---- Кубок міста: two days, set course, eight age groups ------------------------------------------------------

    private sealed record CupGroup(string Name, bool Female, int MinYear, int MaxYear, int? MinBirth, int? MaxBirth,
        decimal Fee, int Controls1, int Controls2, double Minutes1, double Minutes2, int Runners, int Bib);

    private static readonly CupGroup[] CupGroups =
    [
        new("Ч12", false, 2014, 2016, 2014, null, 100, 7, 8, 18, 24, 6, 101),
        new("Ж12", true, 2014, 2016, 2014, null, 100, 7, 8, 20, 26, 6, 201),
        new("Ч16", false, 2010, 2013, 2010, null, 150, 10, 11, 22, 30, 7, 301),
        new("Ж16", true, 2010, 2013, 2010, null, 150, 9, 10, 24, 32, 7, 401),
        new("Ч21", false, 1988, 2005, null, null, 250, 15, 17, 24, 38, 9, 501),
        new("Ж21", true, 1990, 2005, null, null, 250, 13, 15, 26, 40, 8, 601),
        new("Ч40", false, 1966, 1986, null, 1986, 200, 11, 13, 25, 36, 6, 701),
        new("Ж40", true, 1968, 1986, null, 1986, 200, 10, 11, 27, 38, 5, 801),
    ];

    private static async Task BuildCupAsync()
    {
        var b = await DemoBuilder.CreateAsync(Core, Cup, "Кубок міста з орієнтування 2026", "Київ",
            new DateTimeOffset(2026, 5, 16, 0, 0, 0, Kyiv), 2, DisciplineId.SetCourse, seed: 2026);
        await b.SetDayAsync(1, "Голосіївський парк");
        await b.SetDayAsync(2, "Феофанія");
        await OfficialsAsync(b, "Міська федерація спортивного орієнтування");
        await b.UpdateInfoAsync(info =>
        {
            info.RaisedFeeEnabled = true;
            info.RaisedFeeAmount = 350;      // the late-entry fee replaces the group fee
            info.ChipRentalPricePerDay = 30;
        });
        await b.AddDirectoriesAsync(regionCount: 6, clubCount: 8);

        var codes = Enumerable.Range(31, 26).Select(c => c.ToString()).ToList();
        await b.AddControlsAsync(1, codes, lat: 50.384, lon: 30.512, spread: 0.0045);    // a park sprint
        await b.AddControlsAsync(2, codes, lat: 50.352, lon: 30.493, spread: 0.009);     // a forest middle

        var rentals = Enumerable.Range(0, 16).Select(i => (7_200_101 + i).ToString()).ToList();
        await b.AddRentalChipsAsync(rentals);
        var rentalQueue = new Queue<string>(rentals);

        foreach (var g in CupGroups)
        {
            var course1 = b.LoopCourse(1, codes, g.Controls1);
            var course2 = b.LoopCourse(2, codes, g.Controls2);
            await b.AddGroupAsync(g.Name, day => day == 1 ? course1 : course2,
                group => { group.MinBirthYear = g.MinBirth; group.MaxBirthYear = g.MaxBirth; group.EntryFee = g.Fee; });
        }

        var day1Start = At(10, 0);
        var day2Start = At(11, 0);
        foreach (var g in CupGroups)
        {
            var junior = g.MinBirth is >= 2010;
            for (var i = 0; i < g.Runners; i++)
            {
                var rental = junior && g.MinBirth == 2014;
                var chip = rental ? rentalQueue.Dequeue() : b.ChipNumber(1_020_000);
                var runner = await b.AddRunnerAsync(g.Name, g.Female, g.MinYear + b.Rng.Next(g.MaxYear - g.MinYear + 1),
                    (g.Bib + i).ToString(), chip, null, junior: junior);
                // Lanes: every group starts its own minute sequence (2-minute interval), day 2 in reverse order.
                runner.Links[1].StartTime = day1Start + TimeSpan.FromMinutes(2 * i + Array.IndexOf(CupGroups, g) % 2);
                runner.Links[2].StartTime = day2Start + TimeSpan.FromMinutes(2 * (g.Runners - 1 - i) + Array.IndexOf(CupGroups, g) % 2);
                if (g.Name == "Ч16" && i == 6)
                    runner.Links[1].OutOfCompetition = runner.Links[2].OutOfCompetition = true;
                if (g.Name == "Ч40" && i == 5)
                    runner.Links[1].ResultStatusOverride = FinishStatus.Dns;
                await b.UpdateLinkAsync(runner.Links[1]);
                await b.UpdateLinkAsync(runner.Links[2]);

                for (var day = 1; day <= 2; day++)
                {
                    if (g.Name == "Ч40" && i == 5 && day == 1)
                        continue;                                   // did not start on day 1
                    if (day == 2 && ((g.Name == "Ч21" && i == 1) || (g.Name == "Ж40" && i == 2)))
                        continue;                                   // still on the course on day 2
                    var settings = await b.GroupSettingsAsync(g.Name, day);
                    var punches = settings.CourseOrder.Split(' ').ToList();
                    if ((day == 1 && g.Name == "Ч21" && i == 6) || (day == 1 && g.Name == "Ж16" && i == 5) || (day == 2 && g.Name == "Ж21" && i == 3))
                        punches.RemoveAt(punches.Count / 2);        // a missed control → MP
                    var start = runner.Links[day].StartTime!.Value;
                    await b.AddReadoutAsync(day, chip, punches, start, b.RunTime(day == 1 ? g.Minutes1 : g.Minutes2));
                }
            }
        }

        // A chip nobody in the competition holds, read on day 1 (a borrowed chip the secretary did not enter).
        var unknownCourse = (await b.GroupSettingsAsync("Ч21", 1)).CourseOrder.Split(' ');
        await b.AddReadoutAsync(1, "1099999", unknownCourse, At(10, 31), b.RunTime(24));

        // Late entries on day 2, registered on the spot without a chip yet (what «Сканування чіпів» is for).
        var late = new[] { ("Ч21", false, 1998, "510"), ("Ж21", true, 2001, "609"), ("Ч16", false, 2011, "308") };
        foreach (var (group, female, year, bib) in late)
            await b.AddRunnerAsync(group, female, year, bib, "", At(11, 40), days: [2], junior: year > 2008);
    }

    // ---- За вибором по кількості КП ---------------------------------------------------------------------------------

    private static async Task BuildScoreCountAsync()
    {
        var b = await DemoBuilder.CreateAsync(Core, ScoreCount, "Вибір КП «Пуща-Водиця»", "Пуща-Водиця",
            new DateTimeOffset(2026, 6, 13, 0, 0, 0, Kyiv), 1, DisciplineId.ScoreByCount, seed: 613);
        await OfficialsAsync(b, "Клуб «Лісовик»");
        await b.AddDirectoriesAsync(4, 5);
        var codes = Enumerable.Range(31, 20).Select(c => c.ToString()).ToList();
        await b.AddControlsAsync(1, codes, lat: 50.548, lon: 30.367);
        var allowed = string.Join(' ', codes);
        (string Name, bool Female, int Required, int Runners, int Year)[] groups =
            [("Ч14", false, 8, 6, 2012), ("Ж14", true, 8, 5, 2012), ("Ч21", false, 12, 7, 1995), ("Ж21", true, 10, 6, 1997)];
        foreach (var g in groups)
            await b.AddGroupAsync(g.Name, _ => allowed, settings: s => { s.RequiredControlCount = g.Required; s.DistanceKm = null; });
        foreach (var g in groups)
        {
            for (var i = 0; i < g.Runners; i++)
            {
                var start = At(10, 0) + TimeSpan.FromMinutes(i);
                var runner = await b.AddRunnerAsync(g.Name, g.Female, g.Year + b.Rng.Next(3), $"{(Array.IndexOf(groups, g) + 1) * 100 + i + 1}",
                    b.ChipNumber(1_100_000), start, junior: g.Year > 2008);
                var count = g.Required + (i == 3 ? -2 : b.Rng.Next(3));   // one runner short of the required count
                var punches = b.RandomCourse(codes, count).Split(' ');
                await b.AddReadoutAsync(1, runner.Chip, punches, start, b.RunTime(g.Required * 3.2));
            }
        }
    }

    // ---- За вибором за часом ----------------------------------------------------------------------------------------

    private static async Task BuildScoreTimeAsync()
    {
        var b = await DemoBuilder.CreateAsync(Core, ScoreTime, "Вибір за часом «Голосіїв»", "Голосіївський ліс",
            new DateTimeOffset(2026, 6, 14, 0, 0, 0, Kyiv), 1, DisciplineId.ScoreByTime, seed: 614);
        await OfficialsAsync(b, "Клуб «Азимут»");
        await b.AddDirectoriesAsync(4, 5);
        var codes = new[] { 31, 33, 35, 38, 41, 42, 44, 47, 49, 51, 53, 56, 58, 62, 64, 67, 71, 75, 78, 83 }.Select(c => c.ToString()).ToList();
        await b.AddControlsAsync(1, codes, code => int.Parse(code) / 10, lat: 50.381, lon: 30.505);
        var allowed = string.Join(' ', codes);
        (string Name, bool Female, int Minutes, int Runners, int Year)[] groups =
            [("Ч21", false, 45, 7, 1994), ("Ж21", true, 45, 6, 1996), ("Ч40", false, 40, 5, 1980)];
        foreach (var g in groups)
            await b.AddGroupAsync(g.Name, _ => allowed, settings: s => { s.TimeLimitSeconds = g.Minutes * 60; s.PenaltyPerMinute = 1m; s.DistanceKm = null; });
        foreach (var g in groups)
        {
            for (var i = 0; i < g.Runners; i++)
            {
                var start = At(10, 0) + TimeSpan.FromMinutes(i);
                var runner = await b.AddRunnerAsync(g.Name, g.Female, g.Year + b.Rng.Next(5), $"{(Array.IndexOf(groups, g) + 1) * 100 + i + 1}",
                    b.ChipNumber(1_200_000), start);
                var punches = b.RandomCourse(codes, 7 + b.Rng.Next(8)).Split(' ');
                var run = TimeSpan.FromMinutes(g.Minutes) + TimeSpan.FromSeconds(b.Rng.Next(-600, i % 3 == 0 ? 260 : 30));
                await b.AddReadoutAsync(1, runner.Chip, punches, start, run);
            }
        }
    }

    // ---- Рогейн: teams of two, three hours --------------------------------------------------------------------------

    private static readonly string[] TeamNames =
    [
        "Лісові їжаки", "Північний вітер", "Дві сосни", "Азимут 90", "Карта і компас", "Нічні сови", "Швидкі білки",
        "Туман", "Вовча стежка", "Сім вітрів", "Зелена хвиля", "Борсуки",
    ];

    private static async Task BuildRogaineAsync()
    {
        var b = await DemoBuilder.CreateAsync(Core, Rogaine, "Рогейн «Осінній ліс»", "Броварський ліс",
            new DateTimeOffset(2026, 9, 19, 0, 0, 0, Kyiv), 1, DisciplineId.Rogaine, seed: 919);
        await OfficialsAsync(b, "Клуб «Горизонт»");
        await b.AddDirectoriesAsync(3, 4);
        var codes = Enumerable.Range(0, 30).Select(i => (31 + i * 2 + (i % 3)).ToString()).Distinct().Take(28).ToList();
        await b.AddControlsAsync(1, codes, code => int.Parse(code) / 10, lat: 50.497, lon: 30.795, spread: 0.03);
        var allowed = string.Join(' ', codes);
        (string Name, bool? Female, int Teams)[] groups = [("Чоловіки", false, 5), ("Жінки", true, 3), ("Змішані", null, 4)];
        foreach (var g in groups)
            await b.AddGroupAsync(g.Name, _ => allowed, settings: s => { s.TimeLimitSeconds = 3 * 3600; s.DistanceKm = null; });
        var teamIndex = 0;
        var bib = 1;
        foreach (var g in groups)
        {
            for (var t = 0; t < g.Teams; t++)
            {
                var team = TeamNames[teamIndex++];
                var route = b.RandomCourse(codes, 14 + b.Rng.Next(10)).Split(' ').ToList();
                var run = TimeSpan.FromHours(3) - TimeSpan.FromSeconds(b.Rng.Next(-420, 1500));   // some teams late
                for (var m = 0; m < 2; m++)
                {
                    var female = g.Female ?? m == 1;
                    var runner = await b.AddRunnerAsync(g.Name, female, 1980 + b.Rng.Next(25), (bib++).ToString(),
                        b.ChipNumber(7_300_000), At(10, 0), team: team);
                    var punches = route.ToList();
                    if (m == 1 && t == 1)
                        punches.RemoveAt(3);                         // one member skipped a control → it does not count
                    await b.AddReadoutAsync(1, runner.Chip, punches, At(10, 0), run + TimeSpan.FromSeconds(m * b.Rng.Next(4, 40)));
                }
            }
        }
    }

    // ---- Змішаний: ordered parts + any-of blocks ----------------------------------------------------------------------

    private static async Task BuildMixedAsync()
    {
        var b = await DemoBuilder.CreateAsync(Core, Mixed, "Змішаний напрямок «Конча-Заспа»", "Конча-Заспа",
            new DateTimeOffset(2026, 7, 4, 0, 0, 0, Kyiv), 1, DisciplineId.Mixed, seed: 704);
        await OfficialsAsync(b, "Клуб «Меридіан»");
        await b.AddDirectoriesAsync(4, 5);
        var codes = new[] { "31", "32", "33", "34", "35", "41", "42", "43", "44", "45", "51", "52", "53" }.ToList();
        await b.AddControlsAsync(1, codes, lat: 50.315, lon: 30.565);
        (string Name, bool Female, string Pattern, int Runners, int Year)[] groups =
        [
            ("Ч21", false, "<31 32 33> [3: 41 42 43 44 45] <51 52 53>", 7, 1993),
            ("Ж21", true, "<31 32> [2: 41 42 43 44] <52 53>", 6, 1995),
            ("Ч16", false, "<31 33> [2: 41 42 43] <53>", 5, 2011),
        ];
        foreach (var g in groups)
            await b.AddGroupAsync(g.Name, _ => g.Pattern, settings: s => s.DistanceKm = g.Name == "Ч21" ? 5.8m : g.Name == "Ж21" ? 4.6m : 3.5m);
        foreach (var g in groups)
        {
            for (var i = 0; i < g.Runners; i++)
            {
                var start = At(10, 0) + TimeSpan.FromMinutes(2 * i);
                var runner = await b.AddRunnerAsync(g.Name, g.Female, g.Year + b.Rng.Next(4), $"{(Array.IndexOf(groups, g) + 1) * 100 + i + 1}",
                    b.ChipNumber(1_300_000), start, junior: g.Year > 2008);
                await b.AddReadoutAsync(1, runner.Chip, MixedRun(b, g.Pattern, broken: i == 4), start, b.RunTime(g.Name == "Ч16" ? 26 : 38));
            }
        }
    }

    // A valid run of a "<a b> [n: x y z] <c d>" pattern (any n of the block, shuffled); broken = the first part reversed.
    private static List<string> MixedRun(DemoBuilder b, string pattern, bool broken)
    {
        var result = new List<string>();
        foreach (var part in pattern.Split(['<', '>', '[', ']'], StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).Where(p => p.Length > 0))
        {
            if (part.Contains(':'))
            {
                var n = int.Parse(part[..part.IndexOf(':')]);
                result.AddRange(part[(part.IndexOf(':') + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries).OrderBy(_ => b.Rng.Next()).Take(n));
            }
            else
            {
                var codes = part.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                result.AddRange(broken && result.Count == 0 ? codes.Reverse() : codes);
            }
        }
        return result;
    }

    // ---- Розсіювання: several orders per group ----------------------------------------------------------------------

    private static async Task BuildScatterAsync()
    {
        var b = await DemoBuilder.CreateAsync(Core, Scatter, "Розсіювання «Труханів острів»", "Труханів острів",
            new DateTimeOffset(2026, 7, 5, 0, 0, 0, Kyiv), 1, DisciplineId.Scatter, seed: 705);
        await OfficialsAsync(b, "Клуб «Скіф»");
        await b.AddDirectoriesAsync(4, 5);
        var codes = Enumerable.Range(31, 9).Select(c => c.ToString()).ToList();
        await b.AddControlsAsync(1, codes, lat: 50.462, lon: 30.540, spread: 0.005);
        await b.AddGroupAsync("Ч21", _ => "31 32 33 34 35 36 37 38 39");
        await b.AddGroupAsync("Ж21", _ => "31 32 33 34 35 36");
        await b.SetCourseVariantsAsync("Ч21", 1, ("A", "31 32 33 34 35 36 37 38 39"), ("B", "34 35 36 31 32 33 37 38 39"),
            ("C", "37 38 39 34 35 36 31 32 33"));
        await b.SetCourseVariantsAsync("Ж21", 1, ("A", "31 32 33 34 35 36"), ("B", "34 35 36 31 32 33"));
        var variants = new Dictionary<string, string[]>
        {
            ["Ч21"] = ["31 32 33 34 35 36 37 38 39", "34 35 36 31 32 33 37 38 39", "37 38 39 34 35 36 31 32 33"],
            ["Ж21"] = ["31 32 33 34 35 36", "34 35 36 31 32 33"],
        };
        foreach (var (group, female, count) in new[] { ("Ч21", false, 9), ("Ж21", true, 7) })
        {
            for (var i = 0; i < count; i++)
            {
                var start = At(10, 0) + TimeSpan.FromMinutes(2 * i);
                var runner = await b.AddRunnerAsync(group, female, 1990 + b.Rng.Next(15), $"{(female ? 200 : 100) + i + 1}",
                    b.ChipNumber(1_400_000), start);
                var punches = variants[group][i % variants[group].Length].Split(' ').ToList();
                if (i == 5)
                    (punches[1], punches[2]) = (punches[2], punches[1]);    // an order that matches no variant → MP
                await b.AddReadoutAsync(1, runner.Chip, punches, start, b.RunTime(female ? 30 : 34));
            }
        }
    }

    // ---- Естафета: three legs, mass start ---------------------------------------------------------------------------

    private static async Task BuildRelayAsync()
    {
        var b = await DemoBuilder.CreateAsync(Core, Relay, "Естафета до Дня Незалежності", "Парк «Наталка»",
            new DateTimeOffset(2026, 8, 23, 0, 0, 0, Kyiv), 1, DisciplineId.Relay, seed: 823);
        await OfficialsAsync(b, "Міська федерація спортивного орієнтування");
        await b.AddDirectoriesAsync(4, 6);
        var codes = Enumerable.Range(31, 12).Select(c => c.ToString()).ToList();
        await b.AddControlsAsync(1, codes, lat: 50.497, lon: 30.528, spread: 0.005);
        await b.AddGroupAsync("Ч21", _ => "31 33 35 37 39 41 42", settings: s => { s.LegCount = 3; s.MassStartTime = At(11, 0); });
        await b.AddGroupAsync("Ж21", _ => "32 34 36 38 40 42", settings: s => { s.LegCount = 3; s.MassStartTime = At(11, 0); });
        await b.SetRelaySettingsAsync("Ч21", 1, 3, At(11, 0));
        await b.SetRelaySettingsAsync("Ж21", 1, 3, At(11, 0));

        var store = Core.GetRequiredService<IEventStore>();
        var day = b.Day(1);
        foreach (var (group, female, teams, first) in new[] { ("Ч21", false, 5, 101), ("Ж21", true, 4, 201) })
        {
            var course = (await b.GroupSettingsAsync(group, 1)).CourseOrder.Split(' ').ToList();
            for (var t = 0; t < teams; t++)
            {
                var club = b.Clubs[t % b.Clubs.Count];
                var team = new RelayTeam { EventDayId = day.Id, GroupId = b.Groups[group].Id, Number = first + t, Name = $"{club.Name}{(t >= b.Clubs.Count ? "-2" : "")}" };
                await store.AddRelayTeamsAsync(b.Folder, [team]);
                var legStart = At(11, 0);
                for (var leg = 1; leg <= 3; leg++)
                {
                    var runner = await b.AddRunnerAsync(group, female, 1988 + b.Rng.Next(17), "", b.ChipNumber(1_500_000), null);
                    await store.AssignRelayLegAsync(b.Folder, team.Id, leg, runner.Links[1].Id);
                    if (t == teams - 1 && leg == 3)
                        continue;                                    // the last team's anchor is still out
                    var punches = course.ToList();
                    if (t == 2 && leg == 2)
                        punches.RemoveAt(3);                         // a missed control on leg 2 → the team is MP
                    var run = b.RunTime(female ? 24 : 21);
                    await b.AddReadoutAsync(1, runner.Chip, punches, legStart, run, startPunch: false);
                    legStart += run;
                }
            }
        }
    }
}
