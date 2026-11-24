using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.BusinessLogic.Services.Results;
using static OrientPyx.BusinessLogic.Services.Results.CourseCodes;

namespace OrientPyx.BusinessLogic.Services;

// «Експорт IOF XML» (docs/iof-xml-export-plan.md §2): the raw data of the IOF 3.0 ResultList / StartList of one day
// or several. One snapshot per day, then only computation over it — the same protocol rows, places, relay teams and
// splits the result protocol and the HTML splits show; the group strategies answer here so the builder needs none.
public sealed partial class CompetitionEditorService
{
    public async Task<IofExportData> GetIofExportDataAsync(
        IReadOnlyList<Guid> dayIds, IofListKind kind, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dayIds);
        if (_session.CurrentEvent is null || dayIds.Count == 0)
            return IofExportData.Empty;

        var folder = FolderPath;
        var days = new List<IofDayExport>(dayIds.Count);
        CompetitionInfo? info = null;
        var dayCount = 0;
        foreach (var dayId in dayIds.Distinct())
        {
            var snapshot = await _eventStore.GetDaySnapshotAsync(folder, dayId, cancellationToken);
            var day = snapshot.Days.FirstOrDefault(d => d.Id == dayId);
            if (day is null)
                continue; // a day deleted meanwhile is left out
            info ??= snapshot.Info;
            dayCount = snapshot.Days.Count;
            days.Add(await ReadIofDayAsync(folder, snapshot, day, kind, cancellationToken));
        }
        if (days.Count == 0)
            return IofExportData.Empty;
        days.Sort((a, b) => a.Race.DayNumber.CompareTo(b.Race.DayNumber));

        // Several days of results carry the «по сумі днів» total, by the «Підсумковий протокол» settings (null when the
        // competition has none — the page then seeds them from the app default, as the summary page does).
        SummaryProtocolData? summary = null;
        SummaryProtocolSettings? summarySettings = null;
        if (kind == IofListKind.ResultList && days.Count > 1)
        {
            summary = await GetSummaryProtocolDataAsync(cancellationToken);
            summarySettings = await GetSummaryProtocolSettingsAsync(cancellationToken);
        }

        return new IofExportData(
            new IofEventData((info?.Name ?? string.Empty).Trim(), (info?.Organisation ?? string.Empty).Trim(), dayCount),
            days, summary, summarySettings);
    }

    public async Task<IofExportSettings?> GetIofExportSettingsAsync(CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null)
            return null;

        var json = await _eventStore.GetIofExportJsonAsync(FolderPath, cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<IofExportSettings>(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return null; // a broken row reads as "never set"; the page starts from its defaults
        }
    }

    public Task SaveIofExportSettingsAsync(IofExportSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (_session.CurrentEvent is null)
            return Task.CompletedTask;

        var json = System.Text.Json.JsonSerializer.Serialize(settings);
        return _eventStore.SaveIofExportJsonAsync(FolderPath, json, cancellationToken);
    }

    // One day of the export from its snapshot.
    private async Task<IofDayExport> ReadIofDayAsync(
        string folder, EventDaySnapshot snapshot, EventDay day, IofListKind kind, CancellationToken cancellationToken)
    {
        var settingsByGroup = snapshot.GroupDaySettings.ToDictionary(s => s.GroupId);
        var groupName = snapshot.Groups.ToDictionary(g => g.Id, g => g.Name);
        var inputs = BuildDaySplitInputs(snapshot.ControlPoints);
        var countExcluded = new HashSet<string>(inputs.StartFinishCodes, StringComparer.OrdinalIgnoreCase);
        countExcluded.UnionWith(inputs.DisabledCodes);

        // What each group's discipline says — asked here, so the builder never sees a strategy.
        var classes = new Dictionary<string, IofClassInfo>(TextKey.Comparer);
        foreach (var s in snapshot.GroupDaySettings)
        {
            if (!groupName.TryGetValue(s.GroupId, out var name))
                continue;
            var strategy = _strategies.For(s.DisciplineOverride ?? day.DefaultDiscipline);
            classes.TryAdd(name.Trim(), new IofClassInfo(
                s.DistanceKm,
                ControlCountOf(s, strategy, countExcluded),
                strategy.Competitors,
                strategy.UsesControlPointPoints,
                HasLegCourses: strategy.Competitors == CompetitorModel.TeamWithLegs
                               && s.CourseAssignmentMode != RelayCourseMode.Group));
        }

        var links = DistinctLinks(snapshot.Links);
        ResultProtocolData? results = null;
        StartProtocolData? starts = null;
        IReadOnlyDictionary<Guid, ParticipantDayResult> byLink = new Dictionary<Guid, ParticipantDayResult>();
        IReadOnlyDictionary<Guid, SplitsView> splitsByLink = new Dictionary<Guid, SplitsView>();
        if (kind == IofListKind.ResultList)
        {
            (results, byLink) = await ReadResultProtocolDataAsync(folder, snapshot, day, cancellationToken);
            // The snapshot already holds the day's named courses — no separate read for the variants.
            var variantsByGroup = FinishEvaluator.BuildCourseVariants(
                _strategies, snapshot.CourseVariants, settingsByGroup, day.DefaultDiscipline,
                inputs.StartFinishCodes, inputs.DisabledCodes);
            splitsByLink = BuildDaySplitViews(
                day.DefaultDiscipline, links, snapshot.Participants, settingsByGroup, inputs,
                snapshot.FinishReadouts, variantsByGroup, byLink);
        }
        else
            starts = ReadStartProtocolData(snapshot, day);

        return new IofDayExport(
            IofRaceOf(day, snapshot.Info),
            results,
            starts,
            classes,
            IofRunners(snapshot, day, links, settingsByGroup, inputs, byLink, splitsByLink));
    }

    // Every runner's extras on the day, by participant id: FSOU code, chip, assigned start, the start the discipline
    // resolves for the read-out (a relay leg: its leg start), splits and the named course run.
    private Dictionary<Guid, IofRunnerData> IofRunners(
        EventDaySnapshot snapshot, EventDay day, IReadOnlyList<ParticipantDay> links,
        IReadOnlyDictionary<Guid, GroupDaySettings> settingsByGroup, DaySplitInputs inputs,
        IReadOnlyDictionary<Guid, ParticipantDayResult> byLink, IReadOnlyDictionary<Guid, SplitsView> splitsByLink)
    {
        var participants = snapshot.Participants.ToDictionary(p => p.Id);
        var latestByChip = new Dictionary<string, FinishReadout>(TextKey.Comparer);
        foreach (var r in snapshot.FinishReadouts)
        {
            var key = r.ChipNumber.Trim();
            if (key.Length > 0 && (!latestByChip.TryGetValue(key, out var cur) || r.Order > cur.Order))
                latestByChip[key] = r;
        }
        var variants = snapshot.CourseVariants
            .GroupBy(v => (v.GroupId, Code: TextKey.Of(v.Code)))
            .ToDictionary(g => g.Key, g => g.First());

        var runners = new Dictionary<Guid, IofRunnerData>(links.Count);
        foreach (var link in links)
        {
            if (!participants.TryGetValue(link.ParticipantId, out var p))
                continue;
            byLink.TryGetValue(link.Id, out var result);
            splitsByLink.TryGetValue(link.Id, out var splits);

            DateTimeOffset? resolved = null;
            if (result?.Leg is { } leg && result.Team is not null)
                resolved = leg.Start;
            else if (latestByChip.TryGetValue(link.Chip.Trim(), out var readout))
            {
                GroupDaySettings? gs = link.GroupId is { } g && settingsByGroup.TryGetValue(g, out var s) ? s : null;
                resolved = _strategies.For(gs?.DisciplineOverride ?? day.DefaultDiscipline).ResolveStart(new StartContext
                {
                    ChipStart = readout.StartTime,
                    AssignedStart = link.StartTime,
                    FinishTime = readout.FinishTime
                });
            }

            // The named course run: the scatter variant / leg course the splits were judged against, else (no read-out)
            // the leg's assigned course.
            IofRunnerCourse? course = null;
            var code = splits is { VariantCode.Length: > 0 } ? splits.VariantCode : result?.Leg?.CourseCode;
            if (!string.IsNullOrWhiteSpace(code) && link.GroupId is { } groupId)
            {
                variants.TryGetValue((groupId, TextKey.Of(code)), out var variant);
                var controls = splits is { Layout: SplitsLayout.Ordered } ? splits.ExpectedCount
                    : variant is not null ? CourseControls(variant.CourseOrder, inputs.StartFinishCodes, inputs.DisabledCodes).Count
                    : (int?)null;
                course = new IofRunnerCourse(
                    code.Trim(), variant?.SourceCourseName, variant?.SourceCourseFamily, controls is > 0 ? controls : null);
            }

            runners[p.Id] = new IofRunnerData(p.FsouCode.Trim(), link.Chip.Trim(), link.StartTime, resolved, splits, course);
        }
        return runners;
    }

    // The date the day's times are written on, and its offset: the day's own date, else the competition start + the day
    // number, else today. Read-out dates are not used — Sport Time writes a synthetic 2001-01-01.
    private static IofRaceData IofRaceOf(EventDay day, CompetitionInfo? info)
    {
        DateTimeOffset? dated = day.Date
            ?? (info?.StartDate is { } start ? start.AddDays(Math.Max(0, day.Number - 1)) : null);
        var date = dated is { } d ? DateOnly.FromDateTime(d.DateTime) : DateOnly.FromDateTime(DateTime.Today);
        var offset = dated?.Offset ?? TimeZoneInfo.Local.GetUtcOffset(date.ToDateTime(new TimeOnly(12, 0)));
        return new IofRaceData(day.Id, day.Number, date, offset, day.IsLocked);
    }
}
