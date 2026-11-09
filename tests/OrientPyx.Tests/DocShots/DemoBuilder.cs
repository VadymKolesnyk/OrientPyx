using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.Tests.DocShots;

/// <summary>
/// Writes one demo competition through the real store: fictional people, clubs and regions, control points scattered
/// around a point, read-outs with uneven leg times. Everything is drawn from one seeded <see cref="Random"/>, so a
/// demo is identical on every run (and so are the screenshots of it).
/// </summary>
internal sealed class DemoBuilder
{
    public static readonly TimeSpan Kyiv = TimeSpan.FromHours(3);

    private static readonly string[] Surnames =
    [
        "Коваленко", "Шевченко", "Бондаренко", "Ткаченко", "Кравченко", "Олійник", "Савченко", "Руденко", "Марченко",
        "Поліщук", "Лисенко", "Гончаренко", "Павленко", "Кузьменко", "Клименко", "Романенко", "Василенко", "Петренко",
        "Костенко", "Захарченко", "Литвиненко", "Ковальчук", "Бойко", "Ярошенко", "Мельничук", "Довженко", "Тимошенко",
        "Остапчук", "Гнатюк", "Дячук", "Семенко", "Федоренко", "Юрченко", "Приходько", "Демченко", "Сидоренко",
        "Карпенко", "Левченко", "Онищенко", "Головатюк", "Андрієнко", "Білик", "Вдовиченко", "Гаврилюк", "Дорошенко",
        "Зінченко", "Кириленко", "Лук'яненко", "Мирошниченко", "Назаренко", "Охріменко", "Пилипенко", "Стасюк",
        "Тарасенко", "Устименко", "Фесенко", "Харченко", "Цимбалюк", "Шаповал", "Яковенко",
    ];

    private static readonly string[] Male =
    [
        "Андрій", "Олександр", "Дмитро", "Максим", "Богдан", "Тарас", "Іван", "Олег", "Сергій", "Юрій", "Назар", "Остап",
        "Артем", "Владислав", "Микола", "Роман", "Святослав", "Ярослав", "Денис", "Віталій",
    ];

    private static readonly string[] Female =
    [
        "Олена", "Марія", "Анна", "Софія", "Ірина", "Катерина", "Юлія", "Оксана", "Дарина", "Вікторія", "Наталія",
        "Христина", "Ольга", "Тетяна", "Анастасія", "Соломія", "Зоряна", "Мирослава", "Яна", "Лілія",
    ];

    public static readonly string[] RegionNames =
        ["Київ", "Київська", "Львівська", "Харківська", "Одеська", "Дніпропетровська", "Вінницька", "Житомирська"];

    public static readonly string[] ClubNames =
        ["Азимут", "Компас", "Маршрут", "Лісовик", "Горизонт", "Меридіан", "Скіф", "Вертикаль", "Орієнтир", "Пролісок"];

    private static readonly string[] Coaches = ["Довженко Т.", "Гнатюк І.", "Шаповал О.", "Кириленко В.", "Стасюк Н."];

    private readonly IEventStore _store;
    private readonly HashSet<string> _names = new(StringComparer.Ordinal);
    private readonly HashSet<string> _chips = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, Dictionary<string, (double Lat, double Lon)>> _coords = new();
    private int _groupOrder;
    private int _runnerOrder;
    // Read-out numbers («№» in the finish log) per day, from 1 — the way ImportFinishReadoutsAsync numbers them.
    private readonly Dictionary<int, int> _readoutOrder = new();

    public Random Rng { get; }

    public EventSummary Summary { get; }

    public string Folder => Summary.FolderPath;

    public IReadOnlyList<EventDay> Days { get; }

    public DateTimeOffset Date { get; }

    public Dictionary<string, Group> Groups { get; } = new(StringComparer.Ordinal);

    public List<Region> Regions { get; } = [];

    public List<Club> Clubs { get; } = [];

    private DemoBuilder(IEventStore store, EventSummary summary, IReadOnlyList<EventDay> days, DateTimeOffset date, int seed)
    {
        _store = store;
        Summary = summary;
        Days = days;
        Date = date;
        Rng = new Random(seed);
    }

    public static async Task<DemoBuilder> CreateAsync(IServiceProvider sp, string identifier, string name, string venue,
        DateTimeOffset date, int dayCount, DisciplineId discipline, int seed)
    {
        var catalog = sp.GetRequiredService<IEventCatalogService>();
        var store = sp.GetRequiredService<IEventStore>();
        var summary = await catalog.CreateEventAsync(name, identifier, venue, dayCount, date, date.AddDays(dayCount - 1));
        var days = (await store.GetDaysAsync(summary.FolderPath)).OrderBy(d => d.Number).ToList();
        foreach (var day in days)
        {
            day.DefaultDiscipline = discipline;
            day.Venue = venue;
            await store.UpdateDayAsync(summary.FolderPath, day);
        }
        return new DemoBuilder(store, summary, days, date, seed);
    }

    public EventDay Day(int number) => Days.Single(d => d.Number == number);

    public DateTimeOffset DayDate(int number) => Date.AddDays(number - 1);

    public async Task SetDayAsync(int number, string venue, DisciplineId? discipline = null)
    {
        var day = Day(number);
        day.Venue = venue;
        if (discipline is { } d)
            day.DefaultDiscipline = d;
        await _store.UpdateDayAsync(Folder, day);
    }

    public async Task UpdateInfoAsync(Action<CompetitionInfo> configure)
    {
        var info = await _store.GetCompetitionInfoAsync(Folder) ?? throw new InvalidOperationException("No competition info.");
        configure(info);
        await _store.SaveCompetitionInfoAsync(Folder, info);
    }

    public async Task AddDirectoriesAsync(int regionCount, int clubCount)
    {
        foreach (var name in RegionNames.Take(regionCount))
        {
            var region = new Region { Name = name };
            await _store.AddRegionAsync(Folder, region);
            Regions.Add(region);
        }
        foreach (var name in ClubNames.Take(clubCount))
        {
            var club = new Club { Name = name };
            await _store.AddClubAsync(Folder, club);
            Clubs.Add(club);
        }
    }

    /// <summary>Control points of a day scattered around the venue; <paramref name="points"/> gives a control its score.</summary>
    public async Task AddControlsAsync(int dayNumber, IEnumerable<string> codes, Func<string, int?>? points = null,
        double lat = 50.395, double lon = 30.515, double spread = 0.012)
    {
        var day = Day(dayNumber);
        var coords = new Dictionary<string, (double, double)>(StringComparer.Ordinal);
        var list = new List<ControlPoint>();
        var all = new List<string> { "S1" };
        all.AddRange(codes);
        all.Add("F1");
        for (var i = 0; i < all.Count; i++)
        {
            var code = all[i];
            var (cLat, cLon) = code switch
            {
                "S1" => (lat - 0.006, lon - 0.004),
                "F1" => (lat - 0.005, lon - 0.002),
                _ => (lat + (Rng.NextDouble() - 0.5) * 2 * spread, lon + (Rng.NextDouble() - 0.5) * 3 * spread),
            };
            coords[code] = (cLat, cLon);
            list.Add(new ControlPoint
            {
                EventDayId = day.Id,
                Order = i,
                Code = code,
                Type = code == "S1" ? ControlPointType.Start : code == "F1" ? ControlPointType.Finish : ControlPointType.Regular,
                Latitude = Math.Round(cLat, 6),
                Longitude = Math.Round(cLon, 6),
                Points = code is "S1" or "F1" ? null : points?.Invoke(code),
            });
        }
        await _store.AddControlPointsAsync(Folder, list);
        _coords[day.Id] = coords;
    }

    /// <summary>A course of <paramref name="count"/> distinct controls drawn from <paramref name="from"/> in a random order.</summary>
    public string RandomCourse(IReadOnlyList<string> from, int count) =>
        string.Join(' ', from.OrderBy(_ => Rng.Next()).Take(count));

    /// <summary>
    /// A loop of <paramref name="count"/> controls drawn from <paramref name="from"/>: ordered by the angle around their
    /// centre from a random first control and direction — the shape of a real course rather than a zigzag.
    /// </summary>
    public string LoopCourse(int dayNumber, IReadOnlyList<string> from, int count)
    {
        var coords = _coords[Day(dayNumber).Id];
        var picked = from.OrderBy(_ => Rng.Next()).Take(count).ToList();
        var cLat = picked.Average(c => coords[c].Lat);
        var cLon = picked.Average(c => coords[c].Lon);
        var ordered = picked.OrderBy(c => Math.Atan2(coords[c].Lat - cLat, coords[c].Lon - cLon)).ToList();
        if (Rng.Next(2) == 0)
            ordered.Reverse();
        var first = Rng.Next(ordered.Count);
        return string.Join(' ', ordered.Skip(first).Concat(ordered.Take(first)));
    }

    /// <summary>Straight-line length S1 → controls → F1 in km, rounded to 0.1 (what the groups page computes too).</summary>
    public decimal LengthKm(int dayNumber, string course)
    {
        var coords = _coords[Day(dayNumber).Id];
        var codes = new List<string> { "S1" };
        codes.AddRange(course.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(coords.ContainsKey));
        codes.Add("F1");
        double km = 0;
        for (var i = 1; i < codes.Count; i++)
            km += Haversine(coords[codes[i - 1]], coords[codes[i]]);
        return Math.Round((decimal)km, 1);
    }

    private static double Haversine((double Lat, double Lon) a, (double Lat, double Lon) b)
    {
        const double r = 6371.0;
        var dLat = (b.Lat - a.Lat) * Math.PI / 180;
        var dLon = (b.Lon - a.Lon) * Math.PI / 180;
        var h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(a.Lat * Math.PI / 180) * Math.Cos(b.Lat * Math.PI / 180) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * r * Math.Asin(Math.Sqrt(h));
    }

    /// <summary>Adds a group with a course per day (null = the group is not on that day).</summary>
    public async Task<Group> AddGroupAsync(string name, Func<int, string?> courseByDay, Action<Group>? configure = null,
        Action<GroupDaySettings>? settings = null, bool distanceFromCourse = true)
    {
        var group = new Group { Name = name };
        configure?.Invoke(group);
        await _store.AddGroupAsync(Folder, group);
        Groups[name] = group;
        foreach (var day in Days)
        {
            if (courseByDay(day.Number) is not { } course)
                continue;
            var s = new GroupDaySettings
            {
                EventDayId = day.Id,
                GroupId = group.Id,
                Order = _groupOrder++,
                CourseOrder = course,
                DistanceKm = distanceFromCourse && course.Length > 0 && !course.Contains('<') && !course.Contains('[')
                    ? LengthKm(day.Number, course)
                    : null,
            };
            settings?.Invoke(s);
            await _store.AddGroupDaySettingsAsync(Folder, s);
        }
        return group;
    }

    public async Task<GroupDaySettings> GroupSettingsAsync(string groupName, int dayNumber)
    {
        var all = await _store.GetGroupDaySettingsAsync(Folder, Day(dayNumber).Id);
        return all.Single(s => s.GroupId == Groups[groupName].Id);
    }

    public async Task SetCourseVariantsAsync(string groupName, int dayNumber, params (string Code, string Order)[] variants)
    {
        var day = Day(dayNumber);
        var group = Groups[groupName];
        await _store.ReplaceCourseVariantsForGroupAsync(Folder, day.Id, group.Id, variants
            .Select((v, i) => new GroupCourseVariant { EventDayId = day.Id, GroupId = group.Id, Code = v.Code, CourseOrder = v.Order, Order = i })
            .ToList());
    }

    public async Task SetRelaySettingsAsync(string groupName, int dayNumber, int legCount, TimeSpan massStart)
    {
        var s = await GroupSettingsAsync(groupName, dayNumber);
        await _store.UpdateRelaySettingsAsync(Folder, s.Id, legCount, massStart, "", RelayRestartRanking.AfterClean);
    }

    /// <summary>A unique fictional «Прізвище Ім'я».</summary>
    public string PersonName(bool female)
    {
        while (true)
        {
            var name = $"{Surnames[Rng.Next(Surnames.Length)]} {(female ? Female : Male)[Rng.Next(female ? Female.Length : Male.Length)]}";
            if (_names.Add(name))
                return name;
        }
    }

    /// <summary>A unique chip number in <paramref name="from"/>..<paramref name="from"/>+span (a SportIdent range).</summary>
    public string ChipNumber(int from, int span = 99_999)
    {
        while (true)
        {
            var chip = (from + Rng.Next(span)).ToString(CultureInfo.InvariantCulture);
            if (_chips.Add(chip))
                return chip;
        }
    }

    public string Rank(bool junior) => junior
        ? Pick("", "", "III-ю", "II-ю", "I-ю", "б/р ю")
        : Pick("", "III", "II", "II", "I", "I", "КМСУ", "б/р");

    public string Pick(params string[] values) => values[Rng.Next(values.Length)];

    public async Task AddRentalChipsAsync(IEnumerable<string> numbers)
    {
        var chips = numbers.Select(n => new RentalChip { Number = n }).ToList();
        foreach (var chip in chips)
            _chips.Add(chip.Number);
        await _store.AddRentalChipsAsync(Folder, chips);
    }

    /// <summary>Adds a runner (club and region drawn from the directories) on the given days of the group.</summary>
    public async Task<Runner> AddRunnerAsync(string groupName, bool female, int birthYear, string number, string chip,
        TimeSpan? start, int[]? days = null, string? team = null, bool junior = false, Action<ParticipantDay>? configureDay = null,
        Action<Participant>? configure = null)
    {
        var club = Clubs.Count > 0 ? Clubs[Rng.Next(Clubs.Count)] : null;
        var region = club is not null && Regions.Count > 0 ? Regions[Clubs.IndexOf(club) % Regions.Count] : null;
        var participant = new Participant
        {
            FullName = PersonName(female),
            Number = number,
            BirthDate = new DateTimeOffset(birthYear, 1 + Rng.Next(12), 1 + Rng.Next(28), 0, 0, 0, Kyiv),
            Rank = Rank(junior),
            Coach = Rng.Next(3) == 0 ? "" : Coaches[Rng.Next(Coaches.Length)],
            ClubId = club?.Id,
            RegionId = region?.Id,
            Team = team ?? string.Empty,
        };
        configure?.Invoke(participant);
        await _store.AddParticipantAsync(Folder, participant);

        var links = new Dictionary<int, ParticipantDay>();
        foreach (var day in Days.Where(d => days is null || days.Contains(d.Number)))
        {
            var link = new ParticipantDay
            {
                EventDayId = day.Id,
                ParticipantId = participant.Id,
                Order = _runnerOrder,
                GroupId = Groups[groupName].Id,
                Chip = chip,
                StartTime = start,
            };
            configureDay?.Invoke(link);
            await _store.AddParticipantDayAsync(Folder, link);
            links[day.Number] = link;
        }
        _runnerOrder++;
        return new Runner(participant, chip, links);
    }

    public async Task UpdateLinkAsync(ParticipantDay link) => await _store.UpdateParticipantDayAsync(Folder, link);

    /// <summary>
    /// A chip read-out: <paramref name="punches"/> at uneven leg times that add up to the run, a start punch at
    /// <paramref name="start"/> (null = none, the assigned start is used) and the finish at start + <paramref name="run"/>.
    /// </summary>
    public async Task AddReadoutAsync(int dayNumber, string chip, IReadOnlyList<string> punches, TimeSpan start, TimeSpan run,
        bool startPunch = true, bool finishPunch = true)
    {
        var order = _readoutOrder[dayNumber] = _readoutOrder.GetValueOrDefault(dayNumber) + 1;
        var dayDate = DayDate(dayNumber);
        var startAt = dayDate + start;
        var finishAt = startAt + run;
        var weights = Enumerable.Range(0, punches.Count + 1).Select(_ => 0.55 + Rng.NextDouble()).ToList();
        var total = weights.Sum();
        var at = startAt;
        var times = new List<string>();
        for (var i = 0; i < punches.Count; i++)
        {
            at += TimeSpan.FromSeconds(Math.Round(run.TotalSeconds * weights[i] / total));
            times.Add($"{punches[i]}@{at.UtcTicks}");
        }
        await _store.AddFinishReadoutsAsync(Folder,
        [
            new FinishReadout
            {
                EventDayId = Day(dayNumber).Id,
                Order = order,
                ChipNumber = chip,
                StartTime = startPunch ? startAt : null,
                FinishTime = finishPunch ? finishAt : null,
                Punches = string.Join(' ', punches),
                PunchTimes = string.Join(',', times),
                ContentKey = $"{Summary.Identifier}-{dayNumber}-{chip}-{order}",
                CreatedAt = finishPunch ? finishAt : at.AddMinutes(1),
            },
        ]);
    }

    /// <summary>A run time around <paramref name="minutes"/> (−10 % … +45 %), whole seconds.</summary>
    public TimeSpan RunTime(double minutes) =>
        TimeSpan.FromSeconds(Math.Round(minutes * 60 * (0.9 + Rng.NextDouble() * 0.55)));

    public static TimeSpan At(int hour, int minute, int second = 0) => new(hour, minute, second);

    public sealed record Runner(Participant Participant, string Chip, Dictionary<int, ParticipantDay> Links);
}
