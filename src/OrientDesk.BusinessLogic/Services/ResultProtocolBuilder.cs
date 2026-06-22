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

        // Parallel short captions — the renderer falls back to these for a column too narrow for the full one.
        // Blank when no abbreviation is configured (the renderer then keeps the full caption).
        var shortHeaders = columns
            .Select(c => labels.ColumnHeadersShort is { } s && s.TryGetValue(c, out var h) ? h : string.Empty)
            .ToList();

        // Per-column body-wrap flags (parallel to the columns) — free-text columns may wrap; short-code columns
        // stay on one line. See ColumnBodyWraps.
        var bodyWrap = columns.Select(ColumnBodyWraps).ToList();

        // Per-column shrink priority (parallel to the columns) — drives which columns give up width first when the
        // table is too wide for the page. See ShrinkPriority.
        var shrinkPriority = columns.Select(ShrinkPriority).ToList();

        var sections = new List<ResultProtocolSection>(data.Groups.Count);
        foreach (var group in data.Groups.OrderBy(g => g.Order))
        {
            // Skip empty groups — a group with no participants produces no useful section on the sheet.
            if (group.Rows.Count == 0)
                continue;

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
                // The rank-derivation line is shown only when the group awards a rank AND the «виконаний розряд»
                // column is in the visible set — so it explains a column the reader can actually see.
                RankCalculationText = columns.Contains(ProtocolColumn.AwardedRank)
                    ? FormatRankCalculation(group.RankCalculation, labels)
                    : string.Empty,
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
            ColumnHeadersShort = shortHeaders,
            ColumnBodyWrap = bodyWrap,
            ColumnShrinkPriority = shrinkPriority,
            Sections = sections,
            Officials = ProtocolOfficialsFactory.Build(
                data.Officials, labels.ChiefJudgeLabel, labels.ChiefSecretaryLabel, labels.JuryLabel),
            Footer = ProtocolFooterFactory.Build(
                labels.FooterSoftwareName, labels.FooterGeneratedLabel, labels.FooterPageLabel)
        };
    }

    // Whether a column's BODY text may wrap onto several lines. Free-text columns (name, club, region, sports
    // school, coach) hold arbitrary-length values, so they wrap — sized to the typical content with long
    // outliers wrapping. The short-code columns (№ з/п, number, birth date, rank, result, place, score) hold
    // fixed short tokens that must stay on one line, so they never wrap.
    private static bool ColumnBodyWraps(ProtocolColumn column) => column switch
    {
        ProtocolColumn.FullName => true,
        ProtocolColumn.Club => true,
        ProtocolColumn.Region => true,
        ProtocolColumn.Dussh => true,
        ProtocolColumn.Coach => true,
        _ => false
    };

    // How willingly a column gives up width when the table is too wide for the page (see
    // ResultProtocolDocument.ColumnShrinkPriority): 1 = never narrowed (protected); 2/3/4 = may shrink, ever more
    // willingly (4 first and furthest), but never below a content-derived floor. Tuned to keep the spine of the
    // sheet (№/name/result/place/points/birth-date) readable while the secondary identity columns
    // (ДЮСШ, тренер, клуб, регіон) give way under pressure.
    private static int ShrinkPriority(ProtocolColumn column) => column switch
    {
        ProtocolColumn.BirthDate => 1,
        ProtocolColumn.Result => 1,
        ProtocolColumn.Place => 1,
        ProtocolColumn.Points => 1,
        ProtocolColumn.Sequence => 2,
        ProtocolColumn.Number => 2,
        ProtocolColumn.FullName => 2,
        ProtocolColumn.Rank => 2,
        ProtocolColumn.AwardedRank => 2,
        ProtocolColumn.Score => 2,
        ProtocolColumn.Club => 3,
        ProtocolColumn.Region => 3,
        ProtocolColumn.Dussh => 4,
        ProtocolColumn.Coach => 4,
        _ => 1
    };

    // The rank-derivation line: "Клас дистанції: КМС ; Ранг змагань: 790 балів ; КМСУ 120% 00:24:08 ; I 135%
    // 00:27:09 …" — the course class, the computed course rank, then one segment per attainable rank with its
    // qualifying % and the cut-off it implies (a time for a time discipline, a score for a point-scoring one).
    // Blank when the group has no calc (awards no rank).
    private static string FormatRankCalculation(GroupRankCalculation? calc, ProtocolLabels labels)
    {
        if (calc is null || calc.Entries.Count == 0)
            return string.Empty;

        var parts = new List<string>(calc.Entries.Count + 2);
        if (labels.CourseClassLabel.Length > 0 && calc.CourseClass.Length > 0)
            parts.Add($"{labels.CourseClassLabel}: {calc.CourseClass}");
        var unit = labels.RankPointsUnitLabel.Length > 0 ? $" {labels.RankPointsUnitLabel}" : string.Empty;
        parts.Add($"{labels.CompetitionRankLabel}: {calc.Rank.ToString(CultureInfo.InvariantCulture)}{unit}");

        foreach (var e in calc.Entries)
        {
            var cutoff = e.CutoffTimeSeconds is { } secs
                ? FormatCutoffTime(secs)
                : e.CutoffScore is { } score ? score.ToString(CultureInfo.InvariantCulture) : string.Empty;
            var seg = $"{e.RankName} {e.Percent.ToString(CultureInfo.InvariantCulture)}%";
            if (cutoff.Length > 0)
                seg += $" {cutoff}";
            parts.Add(seg);
        }

        return string.Join(" ; ", parts);
    }

    // A rank cut-off time as "hh:mm:ss" (the time a result must not exceed to earn the rank), matching the
    // printed sheet's two-digit-hour form (e.g. "00:24:08").
    private static string FormatCutoffTime(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Round(seconds));
        return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
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
        ProtocolColumn.Points => lead.Result.Points is { } pts ? PointsTable.Format(pts) : string.Empty,
        _ => string.Empty
    };

    // A team member's cell: the per-person identity columns, but no place/score/sequence (those belong to the
    // team caption above). The member still shows their own result time/status for reference.
    private static string MemberCell(ProtocolColumn column, ResultProtocolRow row) => column switch
    {
        ProtocolColumn.Sequence => string.Empty,
        ProtocolColumn.Place => string.Empty,
        ProtocolColumn.Score => string.Empty,
        ProtocolColumn.Points => string.Empty,
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
        ProtocolColumn.Place => PlaceCell(row.Result),
        ProtocolColumn.Score => row.Result.Score is { } s ? s.ToString(CultureInfo.InvariantCulture) : string.Empty,
        ProtocolColumn.Points => row.Result.Points is { } pts ? PointsTable.Format(pts) : string.Empty,
        ProtocolColumn.AwardedRank => row.Result.AwardedRank ?? string.Empty,
        _ => string.Empty
    };

    // The place column: the 1-based rank, «П/К» for a personal-discipline out-of-competition runner (not
    // placed but still listed), blank otherwise.
    private static string PlaceCell(ParticipantDayResult r) =>
        r.Place is { } p ? p.ToString(CultureInfo.InvariantCulture)
        : r.OutOfCompetition ? ParticipantDayResult.OutOfCompetitionMark
        : string.Empty;

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
