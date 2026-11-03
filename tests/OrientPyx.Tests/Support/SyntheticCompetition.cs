using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Interfaces;

namespace OrientPyx.Tests.Support;

/// <summary>
/// A small, fully deterministic competition built through the real services: two days, two groups with
/// courses, four control points, six runners with chips and start times, and day-1 read-outs covering an
/// OK run, a missed control and a runner still on the course. Its results are approved in
/// <c>Golden/autotest.results.json</c>, so the tests need no real competition data to catch a change.
/// </summary>
public static class SyntheticCompetition
{
    public const string Identifier = "autotest";

    private static readonly TimeSpan Kyiv = TimeSpan.FromHours(3);
    private static readonly DateTimeOffset Day1 = new(2026, 5, 1, 0, 0, 0, Kyiv);

    private sealed record Runner(string Name, string Number, string Chip, string GroupName, int StartMinute,
        string? Punches, int? FinishSecondsAfterStart);

    private static readonly Runner[] Runners =
    [
        new("Петренко Іван", "101", "8001001", "Ч21", 0, "31 32 33 34", 31 * 60 + 5),
        new("Коваль Андрій", "102", "8001002", "Ч21", 1, "31 32 33 34", 29 * 60 + 48),
        new("Шевченко Олег", "103", "8001003", "Ч21", 2, "31 32 34", 27 * 60 + 10),      // missed 33 → MP
        new("Бойко Марія", "201", "8002001", "Ж21", 3, "31 33 34", 35 * 60 + 2),
        new("Ткаченко Олена", "202", "8002002", "Ж21", 4, "31 33 34", 35 * 60 + 2),      // tie with 201
        new("Мельник Софія", "203", "8002003", "Ж21", 5, null, null),                    // no read-out yet
    ];

    public static async Task<string> CreateAsync(IServiceProvider services)
    {
        var catalog = services.GetRequiredService<IEventCatalogService>();
        var store = services.GetRequiredService<IEventStore>();

        var summary = await catalog.CreateEventAsync("Автотест OrientPyx", Identifier, "Полігон", 2, Day1, Day1.AddDays(1));
        var folder = summary.FolderPath;
        var days = (await store.GetDaysAsync(folder)).OrderBy(d => d.Number).ToList();

        var groups = new Dictionary<string, Group>();
        foreach (var name in new[] { "Ч21", "Ж21" })
        {
            var group = new Group { Name = name };
            await store.AddGroupAsync(folder, group);
            groups[name] = group;
        }

        var courses = new Dictionary<(int Day, string Group), string>
        {
            [(1, "Ч21")] = "31 32 33 34",
            [(1, "Ж21")] = "31 33 34",
            [(2, "Ч21")] = "34 33 32 31",
            [(2, "Ж21")] = "34 32 31",
        };

        var order = 0;
        foreach (var day in days)
        {
            var codes = new[] { "31", "32", "33", "34" };
            var points = codes.Select((code, i) => new ControlPoint
            {
                EventDayId = day.Id,
                Order = i,
                Code = code,
                Latitude = 50.45 + i * 0.002,
                Longitude = 30.52 + i * 0.003,
            }).ToList();
            await store.AddControlPointsAsync(folder, points);

            foreach (var (name, group) in groups)
            {
                await store.AddGroupDaySettingsAsync(folder, new GroupDaySettings
                {
                    EventDayId = day.Id,
                    GroupId = group.Id,
                    Order = order++,
                    CourseOrder = courses[(day.Number, name)],
                    DistanceKm = name == "Ч21" ? 3.4m : 2.6m,
                });
            }
        }

        var readouts = new List<FinishReadout>();
        for (var i = 0; i < Runners.Length; i++)
        {
            var r = Runners[i];
            var participant = new Participant { FullName = r.Name, Number = r.Number, BirthDate = new DateTimeOffset(1995 + i, 3, 1, 0, 0, 0, Kyiv) };
            await store.AddParticipantAsync(folder, participant);

            var start = new TimeSpan(10, r.StartMinute, 0);
            foreach (var day in days)
            {
                await store.AddParticipantDayAsync(folder, new ParticipantDay
                {
                    EventDayId = day.Id,
                    ParticipantId = participant.Id,
                    Order = i,
                    GroupId = groups[r.GroupName].Id,
                    Chip = r.Chip,
                    StartTime = start,
                });
            }

            if (r.Punches is null)
                continue;

            var startAt = Day1 + start;
            var finishAt = startAt.AddSeconds(r.FinishSecondsAfterStart!.Value);
            var codes = r.Punches.Split(' ');
            var legSeconds = r.FinishSecondsAfterStart.Value / (codes.Length + 1);
            readouts.Add(new FinishReadout
            {
                EventDayId = days[0].Id,
                Order = readouts.Count,
                ChipNumber = r.Chip,
                StartTime = startAt,
                FinishTime = finishAt,
                Punches = r.Punches,
                PunchTimes = string.Join(',', codes.Select((c, k) => $"{c}@{startAt.AddSeconds(legSeconds * (k + 1)).UtcTicks}")),
                ContentKey = $"autotest-{r.Chip}",
                CreatedAt = finishAt,
            });
        }
        await store.AddFinishReadoutsAsync(folder, readouts);

        return Identifier;
    }
}
