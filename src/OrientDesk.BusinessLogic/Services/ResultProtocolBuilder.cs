using System.Globalization;
using OrientDesk.BusinessLogic.Enums;
using OrientDesk.BusinessLogic.Interfaces;
using OrientDesk.BusinessLogic.Models;

namespace OrientDesk.BusinessLogic.Services;

/// <summary>
/// Default <see cref="IResultProtocolBuilder"/>. Resolves the header (the caller has already folded the
/// competition fallbacks into the settings), picks the visible columns in the configured order, formats one
/// cell per column per row, and orders each group's rows (placed finishers first by ascending place, then the
/// unplaced rest by name). The result formatting mirrors the on-screen result columns: an OK run shows its
/// result time, anything else shows the status code (DNS / MP / …).
/// </summary>
public sealed class ResultProtocolBuilder : IResultProtocolBuilder
{
    public ResultProtocolDocument Build(ResultProtocolData data, ResultProtocolSettings settings, ProtocolLabels labels)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(labels);

        // The visible columns in the configured order — drives both the header row and each cell row.
        var columns = settings.Columns
            .Where(c => c.Visible)
            .Select(c => c.Column)
            .ToList();
        // Always have at least the name column, so a stripped-empty config still produces a usable table.
        if (columns.Count == 0)
            columns.Add(ProtocolColumn.FullName);

        var headers = columns
            .Select(c => labels.ColumnHeaders.TryGetValue(c, out var h) ? h : c.ToString())
            .ToList();

        var sections = new List<ResultProtocolSection>(data.Groups.Count);
        foreach (var group in data.Groups.OrderBy(g => g.Order))
        {
            var rows = group.IsTeam
                ? BuildTeamRows(group.Rows, columns)
                : BuildPersonalRows(group.Rows, columns);

            sections.Add(new ResultProtocolSection
            {
                GroupName = group.Name,
                DistanceText = group.DistanceKm is { } km
                    ? $"{labels.DistanceLabel}: {km.ToString("0.000", CultureInfo.InvariantCulture)} км"
                    : string.Empty,
                ControlCountText = group.ControlCount is { } cc ? $"{cc} {labels.ControlCountLabel}" : string.Empty,
                TimeLimitText = group.TimeLimitSeconds is { } secs and > 0
                    ? $"{labels.TimeLimitLabel}: {FormatTimeLimit(secs)}"
                    : string.Empty,
                CourseSetterText = FormatCourseSetter(
                    labels.CourseSetterLabel, group.CourseSetter, group.CourseSetterCategory),
                Rows = rows
            });
        }

        var title = settings.Title.Trim().Length > 0 ? settings.Title.Trim() : labels.DefaultTitle;

        return new ResultProtocolDocument
        {
            Orientation = settings.Orientation,
            CompetitionName = settings.CompetitionName.Trim(),
            Title = title,
            Subtitle = settings.Subtitle.Trim(),
            Venue = settings.Venue.Trim(),
            DateText = settings.DateText.Trim(),
            CompetitionType = settings.CompetitionType.Trim(),
            ColumnHeaders = headers,
            Sections = sections,
            Officials = ProtocolOfficialsFactory.Build(
                data.Officials, labels.ChiefJudgeLabel, labels.ChiefSecretaryLabel, labels.JuryLabel)
        };
    }

    // "Начальник дистанції: Рачук Тарас" (with " (категорія)" appended when a category is given), or blank
    // when no course-setter is configured for the group.
    private static string FormatCourseSetter(string label, string name, string category)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            return string.Empty;
        var cat = (category ?? string.Empty).Trim();
        var who = cat.Length > 0 ? $"{trimmed} ({cat})" : trimmed;
        return label.Length > 0 ? $"{label}: {who}" : who;
    }

    // A personal (non-team) section: placed finishers first by ascending place, then everyone else by name
    // (the classic protocol order), each as one numbered participant row.
    private static List<ResultProtocolBodyRow> BuildPersonalRows(
        IReadOnlyList<ResultProtocolRow> rows, IReadOnlyList<ProtocolColumn> columns)
    {
        var ordered = rows
            .OrderBy(r => r.Result.Place is { } p ? p : int.MaxValue)
            .ThenBy(r => r.FullName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var body = new List<ResultProtocolBodyRow>(ordered.Count);
        var seq = 0;
        foreach (var row in ordered)
        {
            seq++;
            body.Add(new ResultProtocolBodyRow(columns.Select(c => Cell(c, row, seq)).ToList()));
        }
        return body;
    }

    // A teamed (rogaine) section: group members by team, order teams by their place (then score desc, then
    // team name), and emit a bold team-caption row (carrying the team place/score) above each team's member
    // rows. Teamless runners (поза конкурсом) are listed last, each as their own caption-less row.
    private static List<ResultProtocolBodyRow> BuildTeamRows(
        IReadOnlyList<ResultProtocolRow> rows, IReadOnlyList<ProtocolColumn> columns)
    {
        var body = new List<ResultProtocolBodyRow>();

        // Teamed rows grouped by team name; teamless rows handled separately at the end.
        var teams = rows
            .Where(r => r.Team.Length > 0)
            .GroupBy(r => r.Team, StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(g => g.Min(r => r.Result.Place) is { } p ? p : int.MaxValue)
            .ThenByDescending(g => g.Max(r => r.Result.Score) ?? 0)
            .ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var teamSeq = 0;
        foreach (var team in teams)
        {
            teamSeq++;
            // The team place/score is stamped on every member; take it from the first member. The caption
            // row shows the team name (in the name column), its place and score, and the team sequence no.
            var lead = team.First();
            body.Add(new ResultProtocolBodyRow(
                columns.Select(c => TeamCell(c, lead, team.Key, teamSeq)).ToList(),
                IsTeamHeader: true,
                TeamName: team.Key));

            // Members under the team, by name — each as a plain row with no per-person place (the team has it).
            foreach (var member in team.OrderBy(r => r.FullName, StringComparer.CurrentCultureIgnoreCase))
                body.Add(new ResultProtocolBodyRow(columns.Select(c => MemberCell(c, member)).ToList()));
        }

        // Teamless runners (поза конкурсом): one row each, after the teams, by name.
        foreach (var row in rows.Where(r => r.Team.Length == 0)
                     .OrderBy(r => r.FullName, StringComparer.CurrentCultureIgnoreCase))
            body.Add(new ResultProtocolBodyRow(columns.Select(c => MemberCell(c, row)).ToList()));

        return body;
    }

    // A team caption row's cell: the team name in the name column, the team place/score/result in theirs,
    // the team sequence in the №-column; the per-person columns (birth, club, coach…) are blank on the caption.
    private static string TeamCell(ProtocolColumn column, ResultProtocolRow lead, string teamName, int teamSeq) => column switch
    {
        ProtocolColumn.Sequence => teamSeq.ToString(CultureInfo.InvariantCulture),
        ProtocolColumn.FullName => teamName,
        ProtocolColumn.Result => ResultCell(lead.Result),
        ProtocolColumn.Place => lead.Result.Place is { } p ? p.ToString(CultureInfo.InvariantCulture) : string.Empty,
        ProtocolColumn.Score => lead.Result.Score is { } s ? s.ToString(CultureInfo.InvariantCulture) : string.Empty,
        _ => string.Empty
    };

    // A team member's cell: the per-person identity columns, but no place/score/sequence (those belong to the
    // team caption above). The member still shows their own result time/status for reference.
    private static string MemberCell(ProtocolColumn column, ResultProtocolRow row) => column switch
    {
        ProtocolColumn.Sequence => string.Empty,
        ProtocolColumn.Place => string.Empty,
        ProtocolColumn.Score => string.Empty,
        _ => Cell(column, row, 0)
    };

    private static string Cell(ProtocolColumn column, ResultProtocolRow row, int sequence) => column switch
    {
        ProtocolColumn.Sequence => sequence.ToString(CultureInfo.InvariantCulture),
        ProtocolColumn.Number => row.Number,
        ProtocolColumn.FullName => row.FullName,
        ProtocolColumn.BirthDate => row.BirthDate is { } d ? d.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) : string.Empty,
        ProtocolColumn.Club => row.ClubName,
        ProtocolColumn.Region => row.RegionName,
        ProtocolColumn.Dussh => row.DusshName,
        ProtocolColumn.Coach => row.Coach,
        ProtocolColumn.Rank => row.Rank,
        ProtocolColumn.Result => ResultCell(row.Result),
        ProtocolColumn.Place => row.Result.Place is { } p ? p.ToString(CultureInfo.InvariantCulture) : string.Empty,
        ProtocolColumn.Score => row.Result.Score is { } s ? s.ToString(CultureInfo.InvariantCulture) : string.Empty,
        _ => string.Empty
    };

    // An OK run shows its result time ("h:mm:ss"); any other status shows the standard short code (DNS / MP /
    // …); a blank status (no read-out, no override) shows nothing.
    private static string ResultCell(ParticipantDayResult r)
    {
        if (r.Status == FinishStatus.Ok)
            return r.ResultTime is { } e && e >= TimeSpan.Zero ? e.ToString("h\\:mm\\:ss") : string.Empty;
        return ShortCode(r.Status);
    }

    // The standard language-neutral competition status code (mirrors FinishStatusOptions.ShortCode in the
    // Presentation layer; duplicated here to keep BusinessLogic free of a UI dependency).
    private static string ShortCode(FinishStatus status) => status switch
    {
        FinishStatus.Ok => "OK",
        FinishStatus.Mp => "MP",
        FinishStatus.Ovt => "OVT",
        FinishStatus.Dnf => "DNF",
        FinishStatus.Dns => "DNS",
        FinishStatus.Dsq => "DSQ",
        _ => string.Empty
    };

    // Time limit as "H:mm:ss" (e.g. 24:00:00 for a 24-hour control time).
    private static string FormatTimeLimit(int seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}";
    }
}
