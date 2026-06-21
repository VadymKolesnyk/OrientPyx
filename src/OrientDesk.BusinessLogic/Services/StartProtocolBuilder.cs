using System.Globalization;
using OrientDesk.BusinessLogic.Interfaces;
using OrientDesk.BusinessLogic.Models;

namespace OrientDesk.BusinessLogic.Services;

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

        var sections = kind == StartProtocolKind.Judges
            ? BuildJudgesSections(data, columns, labels)
            : BuildRegularSections(data, columns);

        var title = settings.Title.Trim().Length > 0 ? settings.Title.Trim() : labels.DefaultTitle;

        return new ResultProtocolDocument
        {
            Orientation = settings.Orientation,
            Title = title,
            Subtitle = settings.Subtitle.Trim(),
            Venue = settings.Venue.Trim(),
            DateText = settings.DateText.Trim(),
            CompetitionType = settings.CompetitionType.Trim(),
            ColumnHeaders = headers,
            Sections = sections
        };
    }

    // Regular: one section per group; members ordered by start time (drawn first, undrawn last), then name.
    private static List<ResultProtocolSection> BuildRegularSections(
        StartProtocolData data, IReadOnlyList<StartProtocolColumn> columns)
    {
        var sections = new List<ResultProtocolSection>(data.Groups.Count);
        foreach (var group in data.Groups.OrderBy(g => g.Order))
        {
            var ordered = group.Rows
                .OrderBy(r => r.StartTime ?? TimeSpan.MaxValue)
                .ThenBy(r => r.FullName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            var body = new List<ResultProtocolBodyRow>(ordered.Count);
            var seq = 0;
            foreach (var row in ordered)
            {
                seq++;
                body.Add(new ResultProtocolBodyRow(columns.Select(c => Cell(c, row, seq)).ToList()));
            }

            sections.Add(new ResultProtocolSection { GroupName = group.Name, Rows = body });
        }
        return sections;
    }

    // Judges: one section per start minute (across all groups), members ordered by name; undrawn runners
    // form a trailing "no start time" section. A minute caption row is the section's GroupName.
    private static List<ResultProtocolSection> BuildJudgesSections(
        StartProtocolData data, IReadOnlyList<StartProtocolColumn> columns, StartProtocolLabels labels)
    {
        var all = data.Groups.SelectMany(g => g.Rows).ToList();

        var sections = new List<ResultProtocolSection>();

        // Drawn runners, grouped by whole-minute start, in time order.
        var byMinute = all
            .Where(r => r.StartTime is { } t && t >= TimeSpan.Zero)
            .GroupBy(r => WholeMinute(r.StartTime!.Value))
            .OrderBy(g => g.Key);

        foreach (var minute in byMinute)
        {
            var ordered = minute
                .OrderBy(r => r.FullName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            var body = new List<ResultProtocolBodyRow>(ordered.Count);
            var seq = 0;
            foreach (var row in ordered)
            {
                seq++;
                body.Add(new ResultProtocolBodyRow(columns.Select(c => Cell(c, row, seq)).ToList()));
            }

            sections.Add(new ResultProtocolSection { GroupName = FormatMinute(minute.Key), Rows = body });
        }

        // Undrawn runners (no start time), last, by name.
        var undrawn = all
            .Where(r => r.StartTime is null)
            .OrderBy(r => r.FullName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (undrawn.Count > 0)
        {
            var body = new List<ResultProtocolBodyRow>(undrawn.Count);
            var seq = 0;
            foreach (var row in undrawn)
            {
                seq++;
                body.Add(new ResultProtocolBodyRow(columns.Select(c => Cell(c, row, seq)).ToList()));
            }
            sections.Add(new ResultProtocolSection { GroupName = labels.NoStartTimeCaption, Rows = body });
        }

        return sections;
    }

    // Truncate to the whole minute the runner starts on (drops seconds), so everyone starting that minute
    // groups together. Start times are time-of-day, so this stays within a day.
    private static TimeSpan WholeMinute(TimeSpan t) => TimeSpan.FromMinutes(Math.Floor(t.TotalMinutes));

    private static string FormatMinute(TimeSpan t) => t.ToString(@"hh\:mm", CultureInfo.InvariantCulture);

    private static string Cell(StartProtocolColumn column, StartProtocolRow row, int sequence) => column switch
    {
        StartProtocolColumn.StartTime => row.StartTime is { } t ? t.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture) : string.Empty,
        StartProtocolColumn.Sequence => sequence.ToString(CultureInfo.InvariantCulture),
        StartProtocolColumn.Number => row.Number,
        StartProtocolColumn.FullName => row.FullName,
        // A start protocol traditionally shows just the birth year, not the full date.
        StartProtocolColumn.BirthDate => row.BirthDate is { } d ? d.Year.ToString(CultureInfo.InvariantCulture) : string.Empty,
        StartProtocolColumn.Club => row.ClubName,
        StartProtocolColumn.Region => row.RegionName,
        StartProtocolColumn.Dussh => row.DusshName,
        StartProtocolColumn.Coach => row.Coach,
        StartProtocolColumn.Rank => row.Rank,
        StartProtocolColumn.Chip => row.Chip,
        StartProtocolColumn.Group => row.GroupName,
        _ => string.Empty
    };
}
