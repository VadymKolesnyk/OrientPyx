using System.Globalization;
using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.BusinessLogic.Disciplines.Plugins;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.BusinessLogic.Services.Results;

namespace OrientPyx.BusinessLogic.Services;

// Plugin reports (Р-63…Р-66): the report's settings and its document, built from the same computed results the
// protocols show. Nothing is stored but the settings (PluginSettingsRow under the report id).
public sealed partial class CompetitionEditorService
{
    public async Task<PluginReportSettingsData?> GetPluginReportSettingsAsync(
        string reportId, Guid? dayId, CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null || PluginReports.Find(_plugins, reportId) is not { } report)
            return null;

        var defs = report.StoredSettings;
        if (defs.Count == 0)
            return new PluginReportSettingsData(report.Id, report.PluginId, [], new Dictionary<string, object?>());

        var rows = await _eventStore.GetDisciplineSettingsAsync(FolderPath, cancellationToken);
        var values = ResolveReportSettings(report, rows, dayId);
        return new PluginReportSettingsData(report.Id, report.PluginId, defs,
            defs.ToDictionary(d => d.Key, d => values[d.Key]));
    }

    public async Task SavePluginReportSettingsAsync(
        string reportId, Guid? dayId, IReadOnlyDictionary<string, object?> values,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (_session.CurrentEvent is null || PluginReports.Find(_plugins, reportId) is not { } report)
            return;

        var defs = report.StoredSettings;
        var coerced = defs.ToDictionary(d => d.Key,
            d => values.TryGetValue(d.Key, out var v) ? DisciplineSettings.Coerce(v, d) : null);
        var owner = new DisciplineId(report.Id);
        var daySettings = defs.Where(d => d.Scope == SettingScope.Day).ToList();
        var competitionSettings = defs.Where(d => d.Scope == SettingScope.Competition).ToList();
        if (daySettings.Count > 0 && dayId is { } day)
            await _eventStore.SaveDisciplineSettingsAsync(FolderPath, day, owner,
                DisciplineSettings.Serialize(coerced, daySettings), cancellationToken);
        if (competitionSettings.Count > 0)
            await _eventStore.SaveDisciplineSettingsAsync(FolderPath, null, owner,
                DisciplineSettings.Serialize(coerced, competitionSettings), cancellationToken);
    }

    public async Task<PluginReportOutput> BuildPluginReportAsync(
        string reportId, Guid? dayId, PluginReportLabels labels, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(labels);
        if (_session.CurrentEvent is null || PluginReports.Find(_plugins, reportId) is not { } report)
            return PluginReportOutput.None;

        var folder = FolderPath;
        var days = (await _eventStore.GetDaysAsync(folder, cancellationToken)).OrderBy(d => d.Number).ToList();
        var day = report.Scope == ReportScope.Day ? days.FirstOrDefault(d => d.Id == dayId) : null;
        if (report.Scope == ReportScope.Day && day is null)
            return PluginReportOutput.None;
        var covered = day is null ? days : [day];

        var info = await _eventStore.GetCompetitionInfoAsync(folder, cancellationToken);
        var participants = await _eventStore.GetParticipantsAsync(folder, cancellationToken);
        var views = new ParticipantViews(
            participants,
            await _eventStore.GetRegionsAsync(folder, cancellationToken),
            await _eventStore.GetClubsAsync(folder, cancellationToken),
            await _eventStore.GetDusshesAsync(folder, cancellationToken),
            await _eventStore.GetParticipantFieldValuesAsync(folder, cancellationToken));

        var reportDays = new List<ReportDay>(covered.Count);
        foreach (var d in covered)
            reportDays.Add(ReportDayOf(d, await GetResultProtocolDataAsync(d.Id, cancellationToken), views));

        var settingRows = await _eventStore.GetDisciplineSettingsAsync(folder, cancellationToken);
        var context = new ReportContext
        {
            CompetitionName = (info?.Name ?? string.Empty).Trim(),
            Days = reportDays,
            Day = day is null ? null : reportDays[0],
            Participants = participants
                .OrderBy(p => p.FullName, StringComparer.CurrentCultureIgnoreCase)
                .Select(p => views.For(p.Id))
                .ToList(),
            Settings = ResolveReportSettings(report, settingRows, day?.Id),
            Strings = key => labels.Text(PluginStrings.Key(report.PluginId, key))
        };

        ReportDocument built;
        try
        {
            built = report.Report.Build(context) ?? new ReportDocument();
        }
        catch (Exception ex)
        {
            // Р-65: a failing report shows its error on the page; nothing else is affected.
            return new PluginReportOutput(null, $"{ex.GetType().Name}: {ex.Message}");
        }

        var header = new PluginReportHeader(
            (info?.Organisation ?? string.Empty).Trim(),
            (info?.Name ?? string.Empty).Trim(),
            day is null ? CompetitionDates(info, days) : DateOf(day.Date),
            FirstNonBlank(day?.Venue, info?.Venue));
        return new PluginReportOutput(
            PluginReportDocumentBuilder.Build(built, report, header, OfficialsFrom(info), labels), null);
    }

    private static SettingValues ResolveReportSettings(
        PluginReportInfo report, IEnumerable<PluginSettingsRow> rows, Guid? dayId) =>
        DisciplineSettings.Resolve(new DisciplineId(report.Id), report.StoredSettings, rows, dayId);

    // One day as a report sees it: its groups in the day order, each with its runners (placed first by place, then
    // the rest by name) and, in a relay, its teams.
    private static ReportDay ReportDayOf(EventDay day, ResultProtocolData data, ParticipantViews views)
    {
        ReportResult ResultOf(ResultProtocolRow row)
        {
            var r = row.Result;
            return new ReportResult
            {
                Participant = views.For(row.ParticipantId),
                Status = r.Status,
                Time = r.ResultTime,
                AdjustedTime = r.AdjustedTime,
                Score = r.Score,
                Place = r.Place,
                Points = r.Points,
                OutOfCompetition = r.OutOfCompetition,
                UnrankedReason = r.UnrankedReason,
                AwardedRank = r.AwardedRank,
                TeamNumber = r.Team is { } team ? team.Number ?? 0 : null,
                Leg = r.Leg?.Leg,
                Values = r.Extra
            };
        }

        var groups = new List<ReportGroup>(data.Groups.Count);
        foreach (var group in data.Groups.OrderBy(g => g.Order))
        {
            var results = group.Rows
                .OrderBy(r => r.Result.Place is null)
                .ThenBy(r => r.Result.Place)
                .ThenBy(r => r.FullName, StringComparer.CurrentCultureIgnoreCase)
                .Select(ResultOf)
                .ToList();
            var teams = (group.RelayTeams ?? [])
                .OrderBy(t => t.Result?.Place is null)
                .ThenBy(t => t.Result?.Place)
                .ThenBy(t => RelayTeamNumbers.SortKey(t.Number))
                .Select(t => new ReportTeam
                {
                    Number = t.Number ?? 0,
                    Name = t.Name,
                    Club = t.ClubName,
                    Region = t.RegionName,
                    Status = t.Result?.Status ?? FinishStatus.None,
                    Time = t.Result?.Time,
                    Place = t.Result?.Place,
                    Points = t.Result?.Points,
                    OutOfCompetition = t.Result?.OutOfCompetition ?? false,
                    Legs = t.Legs.OrderBy(l => l.Leg).Select(l => l.Runner is { } runner ? ResultOf(runner) : null).ToList()
                })
                .ToList();
            groups.Add(new ReportGroup
            {
                Name = group.Name,
                Discipline = group.Discipline.Value,
                Results = results,
                Teams = teams
            });
        }

        return new ReportDay
        {
            Number = day.Number,
            Date = day.Date is { } date ? DateOnly.FromDateTime(date.Date) : null,
            Discipline = day.DefaultDiscipline.Value,
            Groups = groups
        };
    }

    private static string DateOf(DateTimeOffset? date) =>
        date is { } d ? d.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) : string.Empty;

    // A competition report's date line: the competition's dates, else the first and last day's.
    private static string CompetitionDates(CompetitionInfo? info, IReadOnlyList<EventDay> days)
    {
        var first = info?.StartDate ?? days.Select(d => d.Date).FirstOrDefault(d => d is not null);
        var last = info?.EndDate ?? days.Select(d => d.Date).LastOrDefault(d => d is not null);
        if (first is null)
            return DateOf(last);
        if (last is null || last.Value.Date == first.Value.Date)
            return DateOf(first);
        return DateOf(first) + " – " + DateOf(last);
    }

    private static string FirstNonBlank(params string?[] values) =>
        values.Select(v => v?.Trim()).FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? string.Empty;
}
