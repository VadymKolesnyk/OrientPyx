using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.Tests.Support;

/// <summary>
/// Builds a small competition through the real store, for tests that assert concrete results: days, control
/// points, groups with per-day courses and settings, runners with chips and start times, and read-outs. Every
/// time is on <see cref="Date"/> (day 1) plus the day offset, in the Kyiv summer offset, so results are
/// deterministic. One instance = one competition; call <see cref="SelectAsync"/> before reading results.
/// </summary>
public sealed class CompetitionBuilder
{
    public static readonly TimeSpan Offset = TimeSpan.FromHours(3);

    private readonly IServiceProvider _sp;
    private readonly IEventStore _store;
    private int _groupOrder;
    private int _runnerOrder;
    private int _readoutOrder;

    public EventSummary Summary { get; }

    public IReadOnlyList<EventDay> Days { get; }

    public DateTimeOffset Date { get; }

    public string Folder => Summary.FolderPath;

    public Dictionary<string, Group> Groups { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, Participant> Runners { get; } = new(StringComparer.Ordinal);

    private CompetitionBuilder(IServiceProvider sp, EventSummary summary, IReadOnlyList<EventDay> days, DateTimeOffset date)
    {
        _sp = sp;
        _store = sp.GetRequiredService<IEventStore>();
        Summary = summary;
        Days = days;
        Date = date;
    }

    public static async Task<CompetitionBuilder> CreateAsync(IServiceProvider sp, string identifier, int dayCount = 1,
        DisciplineId? discipline = null, DateTimeOffset? date = null, string name = "")
    {
        var catalog = sp.GetRequiredService<IEventCatalogService>();
        var store = sp.GetRequiredService<IEventStore>();
        var start = date ?? new DateTimeOffset(2026, 6, 6, 0, 0, 0, Offset);
        var summary = await catalog.CreateEventAsync(name.Length > 0 ? name : $"Тест {identifier}", identifier, "Полігон",
            dayCount, start, start.AddDays(dayCount - 1));
        var days = (await store.GetDaysAsync(summary.FolderPath)).OrderBy(d => d.Number).ToList();
        if (discipline is { } d)
        {
            foreach (var day in days)
            {
                day.DefaultDiscipline = d;
                await store.UpdateDayAsync(summary.FolderPath, day);
            }
        }
        return new CompetitionBuilder(sp, summary, days, start);
    }

    public EventDay Day(int number) => Days.Single(d => d.Number == number);

    public async Task SetDisciplineAsync(int dayNumber, DisciplineId discipline)
    {
        var day = Day(dayNumber);
        day.DefaultDiscipline = discipline;
        await _store.UpdateDayAsync(Folder, day);
    }

    /// <summary>Adds control points to a day: "31" is a regular control, "S1" / "F1" start and finish.</summary>
    public async Task<List<ControlPoint>> AddControlsAsync(int dayNumber, params string[] codes)
    {
        var day = Day(dayNumber);
        var points = codes.Select((code, i) => new ControlPoint
        {
            EventDayId = day.Id,
            Order = i,
            Code = code,
            Type = code.StartsWith('S') ? ControlPointType.Start : code.StartsWith('F') ? ControlPointType.Finish : ControlPointType.Regular,
            Latitude = 50.45 + i * 0.002,
            Longitude = 30.52 + i * 0.003,
        }).ToList();
        await _store.AddControlPointsAsync(Folder, points);
        return points;
    }

    /// <summary>Adds a group that runs on every day with the given course (same on each day unless a per-day
    /// function is given) and applies <paramref name="configure"/> to each day's settings.</summary>
    public async Task<Group> AddGroupAsync(string name, string course, Action<GroupDaySettings>? configure = null)
        => await AddGroupAsync(name, _ => course, configure);

    public async Task<Group> AddGroupAsync(string name, Func<int, string> courseByDay, Action<GroupDaySettings>? configure = null)
    {
        var group = new Group { Name = name };
        await _store.AddGroupAsync(Folder, group);
        Groups[name] = group;
        foreach (var day in Days)
        {
            var settings = new GroupDaySettings
            {
                EventDayId = day.Id,
                GroupId = group.Id,
                Order = _groupOrder++,
                CourseOrder = courseByDay(day.Number),
                DistanceKm = 3.0m,
            };
            configure?.Invoke(settings);
            await _store.AddGroupDaySettingsAsync(Folder, settings);
        }
        return group;
    }

    public async Task<GroupDaySettings> GetGroupSettingsAsync(string groupName, int dayNumber)
    {
        var all = await _store.GetGroupDaySettingsAsync(Folder, Day(dayNumber).Id);
        return all.Single(s => s.GroupId == Groups[groupName].Id);
    }

    public async Task UpdateGroupSettingsAsync(string groupName, int dayNumber, Action<GroupDaySettings> configure)
    {
        var settings = await GetGroupSettingsAsync(groupName, dayNumber);
        configure(settings);
        await _store.UpdateGroupDaySettingsAsync(Folder, settings);
    }

    /// <summary>Adds a runner to every day (or the given ones) in the group, with the chip and the assigned start.</summary>
    public async Task<Participant> AddRunnerAsync(string name, string number, string chip, string? groupName,
        TimeSpan? start = null, Action<Participant>? configure = null, Action<ParticipantDay>? configureDay = null, int[]? days = null)
    {
        var participant = new Participant
        {
            FullName = name,
            Number = number,
            BirthDate = new DateTimeOffset(1990 + _runnerOrder % 20, 1, 15, 0, 0, 0, Offset),
        };
        configure?.Invoke(participant);
        await _store.AddParticipantAsync(Folder, participant);
        Runners[name] = participant;

        foreach (var day in Days.Where(d => days is null || days.Contains(d.Number)))
        {
            var link = new ParticipantDay
            {
                EventDayId = day.Id,
                ParticipantId = participant.Id,
                Order = _runnerOrder,
                GroupId = groupName is null ? null : Groups[groupName].Id,
                Chip = chip,
                StartTime = start,
            };
            configureDay?.Invoke(link);
            await _store.AddParticipantDayAsync(Folder, link);
        }
        _runnerOrder++;
        return participant;
    }

    public async Task<ParticipantDay> GetLinkAsync(string runnerName, int dayNumber)
    {
        var links = await _store.GetParticipantDaysAsync(Folder, Day(dayNumber).Id);
        return links.Single(l => l.ParticipantId == Runners[runnerName].Id);
    }

    public async Task UpdateLinkAsync(string runnerName, int dayNumber, Action<ParticipantDay> configure)
    {
        var link = await GetLinkAsync(runnerName, dayNumber);
        configure(link);
        await _store.UpdateParticipantDayAsync(Folder, link);
    }

    /// <summary>A read-out on the given day: punches evenly spread between start and finish (clock times of that day).
    /// A null <paramref name="chipStart"/> leaves the chip start empty (the assigned start is used).</summary>
    public async Task<FinishReadout> AddReadoutAsync(int dayNumber, string chip, string punches, TimeSpan? chipStart, TimeSpan finish,
        FinishStatus? manualStatus = null)
    {
        var day = Day(dayNumber);
        var dayDate = Date.AddDays(dayNumber - 1);
        var finishAt = dayDate + finish;
        var startAt = chipStart is { } s ? dayDate + s : finishAt.AddMinutes(-30);
        var codes = punches.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var leg = (finishAt - startAt) / (codes.Length + 1);
        var readout = new FinishReadout
        {
            EventDayId = day.Id,
            Order = _readoutOrder++,
            ChipNumber = chip,
            StartTime = chipStart is null ? null : startAt,
            FinishTime = finishAt,
            Punches = punches,
            PunchTimes = string.Join(',', codes.Select((c, k) => $"{c}@{(startAt + leg * (k + 1)).UtcTicks}")),
            ContentKey = $"{Summary.Identifier}-{chip}-{_readoutOrder}",
            ManualStatus = manualStatus,
            CreatedAt = finishAt,
        };
        await _store.AddFinishReadoutsAsync(Folder, [readout]);
        return readout;
    }

    /// <summary>Creates a relay team in the group and places the named runners on their legs (they must already be
    /// on the day, in the same group).</summary>
    public async Task<RelayTeam> AddRelayTeamAsync(string groupName, int number, string name, int dayNumber = 1,
        params (string Runner, int Leg)[] legs)
    {
        var day = Day(dayNumber);
        var team = new RelayTeam { EventDayId = day.Id, GroupId = Groups[groupName].Id, Number = number, Name = name };
        await _store.AddRelayTeamsAsync(Folder, [team]);
        foreach (var (runner, leg) in legs)
        {
            var link = await GetLinkAsync(runner, dayNumber);
            await _store.AssignRelayLegAsync(Folder, team.Id, leg, link.Id);
        }
        return team;
    }

    /// <summary>Relay settings of a group on a day (the generic settings update drops them, so they go through
    /// the dedicated store methods).</summary>
    public async Task SetRelaySettingsAsync(string groupName, int dayNumber, int legCount, TimeSpan? massStart,
        string restartTimesJson = "", RelayRestartRanking ranking = RelayRestartRanking.AfterClean)
    {
        var settings = await GetGroupSettingsAsync(groupName, dayNumber);
        await _store.UpdateRelaySettingsAsync(Folder, settings.Id, legCount, massStart, restartTimesJson, ranking);
    }

    public async Task SetRelayCourseModeAsync(string groupName, int dayNumber, RelayCourseMode mode, string legCoursesJson = "", string teamCourseSet = "")
    {
        var settings = await GetGroupSettingsAsync(groupName, dayNumber);
        await _store.UpdateRelayCourseSettingsAsync(Folder, settings.Id, mode, legCoursesJson, teamCourseSet);
    }

    public async Task SetCourseVariantsAsync(string groupName, int dayNumber, params (string Code, string Order)[] variants)
    {
        var day = Day(dayNumber);
        var group = Groups[groupName];
        await _store.ReplaceCourseVariantsForGroupAsync(Folder, day.Id, group.Id, variants
            .Select((v, i) => new GroupCourseVariant { EventDayId = day.Id, GroupId = group.Id, Code = v.Code, CourseOrder = v.Order, Order = i })
            .ToList());
    }

    public async Task SelectAsync(int dayNumber = 1)
    {
        var session = _sp.GetRequiredService<ISessionService>();
        await session.SelectAsync(Summary, Day(dayNumber));
    }

    /// <summary>The day's results keyed by runner name (selects the competition/day first).</summary>
    public async Task<Dictionary<string, ParticipantDayResult>> ResultsAsync(int dayNumber = 1)
    {
        await SelectAsync(dayNumber);
        var editor = _sp.GetRequiredService<ICompetitionEditorService>();
        var byId = await editor.GetDayResultsByParticipantAsync(Day(dayNumber).Id);
        return Runners.Where(r => byId.ContainsKey(r.Value.Id)).ToDictionary(r => r.Key, r => byId[r.Value.Id], StringComparer.Ordinal);
    }

    public async Task<ResultProtocolData> ProtocolDataAsync(int dayNumber = 1)
    {
        await SelectAsync(dayNumber);
        return await _sp.GetRequiredService<ICompetitionEditorService>().GetResultProtocolDataAsync(Day(dayNumber).Id);
    }

    public async Task<string> SnapshotAsync()
    {
        await SelectAsync(1);
        var editor = _sp.GetRequiredService<ICompetitionEditorService>();
        var session = _sp.GetRequiredService<ISessionService>();
        return await ResultsSnapshot.BuildAsync(editor, session, Days);
    }

    public static TimeSpan At(int hour, int minute, int second = 0) => new(hour, minute, second);
}
