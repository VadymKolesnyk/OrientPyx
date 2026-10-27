using System.Globalization;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Services;

/// <summary>
/// Default <see cref="IStartProtocolBuilder"/>. Resolves the header (the caller has folded the competition
/// fallbacks into the settings), picks the visible columns in the configured order, and lays the rows out
/// by <see cref="StartProtocolKind"/>:
/// <list type="bullet">
/// <item>Regular: one section per group (day order); members ordered by start time, then by name.</item>
/// <item>Judges: one section per start <b>minute</b> (a runner's whole-minute start), gathering everyone who
/// starts that minute across all groups, ordered by name; a trailing "no start time" section collects the
/// undrawn runners.</item>
/// </list>
/// Produces a <see cref="ResultProtocolDocument"/> so it shares the results-protocol .docx writer and preview.
/// </summary>
public sealed class StartProtocolBuilder : IStartProtocolBuilder
{
    public ResultProtocolDocument Build(
        StartProtocolData data,
        StartProtocolSettings settings,
        StartProtocolKind kind,
        StartProtocolLabels labels)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(labels);

        var columns = settings.Columns
            .Where(c => c.Visible)
            .Select(c => c.Column)
            .ToList();
        if (columns.Count == 0)
            columns.Add(StartProtocolColumn.FullName);

        var headers = columns
            .Select(c => labels.ColumnHeaders.TryGetValue(c, out var h) ? h : c.ToString())
            .ToList();

        // Parallel short captions — the renderer falls back to these for a narrow column. Blank ⇒ keep full.
        var shortHeaders = columns
            .Select(c => labels.ColumnHeadersShort is { } s && s.TryGetValue(c, out var h) ? h : string.Empty)
            .ToList();

        // Per-column body-wrap flags — free-text columns may wrap; short-code columns stay on one line.
        var bodyWrap = columns.Select(ColumnBodyWraps).ToList();

        // Per-column shrink priority — which columns give up width first when the table overflows the page width.
        var shrinkPriority = columns.Select(ShrinkPriority).ToList();

        var sections = kind == StartProtocolKind.Judges
            ? BuildJudgesSections(data, columns, labels)
            : BuildRegularSections(data, columns, labels);

        var title = settings.Title.Trim().Length > 0 ? settings.Title.Trim() : labels.DefaultTitle;

        return new ResultProtocolDocument
        {
            Orientation = settings.Orientation,
            TableBorders = settings.TableBorders,
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
            // The page footer is opt-out: a null Footer makes the renderer skip the колонтитул entirely.
            Footer = settings.PageFooter
                ? ProtocolFooterFactory.Build(
                    labels.FooterSoftwareName, labels.FooterGeneratedLabel, labels.FooterPageLabel)
                : null
        };
    }

    // Regular: one section per group; members ordered by start time (drawn first, undrawn last), then name. A
    // relay group lists its teams instead (by leg 1's start, then number), each with its leg rows under it.
    private static List<ResultProtocolSection> BuildRegularSections(
        StartProtocolData data, IReadOnlyList<StartProtocolColumn> columns, StartProtocolLabels labels)
    {
        var sections = new List<ResultProtocolSection>(data.Groups.Count);
        foreach (var group in data.Groups.OrderBy(g => g.Order))
        {
            // Skip empty groups — a group with no members produces no useful section on the sheet.
            if (group.Rows.Count == 0 && group.RelayTeams is not { Count: > 0 })
                continue;

            var units = Units(group)
                .OrderBy(u => u.Start ?? TimeSpan.MaxValue)
                .ThenBy(u => u.Team is null ? 1 : 0)
                .ThenBy(u => u.Team?.Number ?? 0)
                .ThenBy(u => u.SortName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            var body = new List<ResultProtocolBodyRow>(units.Count);
            var seq = 0;
            foreach (var unit in units)
                Emit(body, unit, columns, labels, ++seq);

            sections.Add(new ResultProtocolSection
            {
                GroupName = group.Name,
                CourseSetterText = FormatCourseSetter(
                    labels.CourseSetterLabel, group.CourseSetter, group.CourseSetterCategory),
                Rows = body
            });
        }
        return sections;
    }

    // One line item of a start sheet: a runner, or a relay team (Р-36) with its legs.
    private sealed record StartUnit(TimeSpan? Start, string SortName, StartProtocolRow? Row, StartProtocolTeam? Team,
        string GroupName);

    // A group's units: its relay teams (when it is a relay group) and every runner row not on a team.
    private static IEnumerable<StartUnit> Units(StartProtocolGroup group)
    {
        if (group.RelayTeams is { } teams)
            foreach (var team in teams)
                yield return new StartUnit(team.StartTime, team.Name, null, team, group.Name);
        foreach (var row in group.Rows)
            yield return new StartUnit(row.StartTime, row.FullName, row, null, group.Name);
    }

    private static void Emit(List<ResultProtocolBodyRow> body, StartUnit unit,
        IReadOnlyList<StartProtocolColumn> columns, StartProtocolLabels labels, int seq)
    {
        if (unit.Row is { } row)
        {
            body.Add(new ResultProtocolBodyRow(columns.Select(c => Cell(c, row, seq)).ToList()));
            return;
        }
        var team = unit.Team!;
        body.Add(new ResultProtocolBodyRow(
            columns.Select(c => TeamCell(c, team, unit.GroupName, seq)).ToList(),
            IsTeamHeader: true,
            TeamName: team.Name));
        foreach (var leg in team.Legs.OrderBy(l => l.Leg))
            body.Add(new ResultProtocolBodyRow(
                columns.Select(c => LegCell(c, team, leg, labels)).ToList(),
                IsSubRow: true));
    }

    // A relay team's caption row: leg 1's start, the sequence, the team number, name, club, region and group.
    private static string TeamCell(StartProtocolColumn column, StartProtocolTeam team, string groupName, int seq) => column switch
    {
        StartProtocolColumn.StartTime => team.StartTime is { } t ? t.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture) : string.Empty,
        StartProtocolColumn.Sequence => seq.ToString(CultureInfo.InvariantCulture),
        StartProtocolColumn.Number => team.Number.ToString(CultureInfo.InvariantCulture),
        StartProtocolColumn.FullName => team.Name.Length > 0 ? team.Name : team.Number.ToString(CultureInfo.InvariantCulture),
        StartProtocolColumn.Club => team.ClubName,
        StartProtocolColumn.Region => team.RegionName,
        StartProtocolColumn.Group => groupName,
        StartProtocolColumn.Team => team.Name,
        _ => string.Empty
    };

    // A leg row under its team: the leg number (100.1) and the runner's identity; the start lives on the team row.
    private static string LegCell(StartProtocolColumn column, StartProtocolTeam team, StartProtocolLeg leg,
        StartProtocolLabels labels) => column switch
    {
        StartProtocolColumn.Number => RelayBib.Format(team.Number, leg.Leg),
        StartProtocolColumn.FullName => leg.Runner?.FullName ?? labels.RelayVacantLeg,
        StartProtocolColumn.StartTime or StartProtocolColumn.Sequence or StartProtocolColumn.Group
            or StartProtocolColumn.Team => string.Empty,
        _ => leg.Runner is { } r ? Cell(column, r, 0) : string.Empty
    };

    // Whether a column's BODY text may wrap. Free-text columns (name, club, region, sports school, coach,
    // group, team) wrap; the short-code columns (start time, № з/п, number, year, rank, chip, note) stay on
    // one line. Mirrors ResultProtocolBuilder.ColumnBodyWraps.
    private static bool ColumnBodyWraps(StartProtocolColumn column) => column switch
    {
        StartProtocolColumn.FullName => true,
        StartProtocolColumn.Club => true,
        StartProtocolColumn.Region => true,
        StartProtocolColumn.Dussh => true,
        StartProtocolColumn.Coach => true,
        StartProtocolColumn.Group => true,
        StartProtocolColumn.Team => true,
        _ => false
    };

    // How willingly a column gives up width when the table is too wide for the page (see
    // ResultProtocolDocument.ColumnShrinkPriority): 1 = never narrowed (protected); 2/3/4 = may shrink, ever more
    // willingly (4 first and furthest), but never below a content-derived floor. The start sheet protects its
    // spine (start time, № з/п, number, year, group) and lets the secondary identity columns (ДЮСШ, тренер,
    // кваліфікація, регіон, нотатка) give way first.
    private static int ShrinkPriority(StartProtocolColumn column) => column switch
    {
        StartProtocolColumn.StartTime => 1,
        StartProtocolColumn.Sequence => 1,
        StartProtocolColumn.Number => 1,
        StartProtocolColumn.BirthDate => 1,
        StartProtocolColumn.Group => 1,
        StartProtocolColumn.FullName => 2,
        StartProtocolColumn.Club => 2,
        StartProtocolColumn.Chip => 2,
        StartProtocolColumn.Region => 3,
        StartProtocolColumn.Note => 3,
        StartProtocolColumn.Dussh => 4,
        StartProtocolColumn.Coach => 4,
        StartProtocolColumn.Rank => 4,
        // Team has no explicit category from the user; treat it as a secondary identity column.
        StartProtocolColumn.Team => 3,
        _ => 1
    };

    // "Начальник дистанції: Рачук Тарас" (category appended in parentheses), or blank when none.
    private static string FormatCourseSetter(string label, string name, string category)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            return string.Empty;
        var cat = (category ?? string.Empty).Trim();
        var who = cat.Length > 0 ? $"{trimmed} ({cat})" : trimmed;
        return label.Length > 0 ? $"{label}: {who}" : who;
    }

    // Judges: one section per start minute (across all groups), members ordered by name; undrawn runners
    // form a trailing "no start time" section. A minute caption row is the section's GroupName. On a relay day the
    // unit is the team (Р-36): it sits under leg 1's start minute with its legs below it.
    private static List<ResultProtocolSection> BuildJudgesSections(
        StartProtocolData data, IReadOnlyList<StartProtocolColumn> columns, StartProtocolLabels labels)
    {
        var all = data.Groups.SelectMany(Units).ToList();

        var sections = new List<ResultProtocolSection>();

        // The «№ з/п» runs CONTINUOUSLY down the whole sheet — it does NOT reset per minute band — so the
        // judge can count entrants across the day at a glance. Carried across every minute section and into the
        // trailing undrawn section.
        var seq = 0;

        // Drawn units, grouped by whole-minute start, in time order.
        var byMinute = all
            .Where(u => u.Start is { } t && t >= TimeSpan.Zero)
            .GroupBy(u => WholeMinute(u.Start!.Value))
            .OrderBy(g => g.Key);

        foreach (var minute in byMinute)
        {
            var ordered = minute
                .OrderBy(u => u.SortName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            var body = new List<ResultProtocolBodyRow>(ordered.Count);
            foreach (var unit in ordered)
                Emit(body, unit, columns, labels, ++seq);

            // The minute caption is a shaded full-width band (IsBanded) so it stands out above its runners.
            sections.Add(new ResultProtocolSection
            {
                GroupName = FormatMinute(minute.Key),
                IsBanded = true,
                Rows = body
            });
        }

        // Undrawn units (no start time), last, by name — the sequence keeps counting from the drawn ones.
        var undrawn = all
            .Where(u => u.Start is null)
            .OrderBy(u => u.SortName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (undrawn.Count > 0)
        {
            var body = new List<ResultProtocolBodyRow>(undrawn.Count);
            foreach (var unit in undrawn)
                Emit(body, unit, columns, labels, ++seq);
            sections.Add(new ResultProtocolSection
            {
                GroupName = labels.NoStartTimeCaption,
                IsBanded = true,
                Rows = body
            });
        }

        return sections;
    }

    // Truncate to the whole minute the runner starts on (drops seconds), so everyone starting that minute
    // groups together. Start times are time-of-day, so this stays within a day.
    private static TimeSpan WholeMinute(TimeSpan t) => TimeSpan.FromMinutes(Math.Floor(t.TotalMinutes));

    // The minute caption on the judges' sheet reads as a full time-of-day band ("11:01:00").
    private static string FormatMinute(TimeSpan t) => TimeFormat.Clock(t);

    private static string Cell(StartProtocolColumn column, StartProtocolRow row, int sequence) => column switch
    {
        StartProtocolColumn.StartTime => row.StartTime is { } t ? t.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture) : string.Empty,
        StartProtocolColumn.Sequence => sequence.ToString(CultureInfo.InvariantCulture),
        StartProtocolColumn.Number => row.Number,
        StartProtocolColumn.FullName => row.FullName,
        StartProtocolColumn.BirthDate => row.BirthDate is { } d ? d.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) : string.Empty,
        StartProtocolColumn.Club => row.ClubName,
        StartProtocolColumn.Region => row.RegionName,
        StartProtocolColumn.Dussh => row.DusshName,
        StartProtocolColumn.Coach => row.Coach,
        StartProtocolColumn.Rank => row.Rank,
        StartProtocolColumn.Chip => row.Chip,
        StartProtocolColumn.Group => row.GroupName,
        StartProtocolColumn.Team => row.Team,
        // A blank note column for the judge to write in on the printed sheet.
        StartProtocolColumn.Note => string.Empty,
        _ => string.Empty
    };
}
