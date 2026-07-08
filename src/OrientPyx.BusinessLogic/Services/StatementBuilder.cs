using System.Globalization;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Services;

/// <summary>
/// Default <see cref="IStatementBuilder"/>. Produces a single flat section (no per-group split) from the
/// participant rows, sorted by chip: rental chips first, then own chips, then by chip number ascending, with
/// chip-less rows last. Own-chip rows get the chip cell marked bold (via
/// <see cref="ResultProtocolBodyRow.BoldCells"/>) so their own chips stand out. The filter summary line is
/// stamped onto the document header. Emits a <see cref="ResultProtocolDocument"/> so the statement reuses the
/// results-protocol writer + live preview.
/// </summary>
public sealed class StatementBuilder : IStatementBuilder
{
    public ResultProtocolDocument Build(
        StatementData data, StatementSettings settings, StatementLabels labels, string filterSummary)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(labels);

        // The visible logical columns in the configured order.
        var logical = settings.Columns
            .Where(c => c.Visible)
            .Select(c => c.Column)
            .ToList();
        // Always have at least the name column, so a stripped-empty config still produces a usable table.
        if (logical.Count == 0)
            logical.Add(StatementColumn.FullName);

        // Expand into a PHYSICAL column plan: every logical column is one physical column except «Старт», which
        // becomes one column per day (day index ≥ 0). A single-day block collapses to one plain «Старт» column.
        // dayIndex indexes into StatementRow.StartTimes / data.DayLabels; -1 for every non-start column.
        var dayCount = data.DayLabels.Count;
        var plan = new List<PlanColumn>(logical.Count);
        foreach (var c in logical)
        {
            if (c == StatementColumn.Start && dayCount > 0)
                for (var d = 0; d < dayCount; d++)
                    plan.Add(new PlanColumn(c, d));
            else if (c != StatementColumn.Start)
                plan.Add(new PlanColumn(c, -1));
            // A «Старт» column with no days at all contributes nothing (dayCount == 0).
        }
        if (plan.Count == 0)
            plan.Add(new PlanColumn(StatementColumn.FullName, -1));

        // A multi-day block heads each column "Старт (Д1)"; a single-day block uses the plain «Старт» caption.
        var startTemplate = labels.StartDayHeaderTemplate;
        string StartHeader(int dayIndex) => dayCount > 1 && startTemplate.Length > 0
            ? string.Format(System.Globalization.CultureInfo.InvariantCulture, startTemplate, data.DayLabels[dayIndex])
            : labels.ColumnHeaders.TryGetValue(StatementColumn.Start, out var h) ? h : StatementColumn.Start.ToString();

        var headers = plan
            .Select(p => p.DayIndex >= 0
                ? StartHeader(p.DayIndex)
                : labels.ColumnHeaders.TryGetValue(p.Column, out var h) ? h : p.Column.ToString())
            .ToList();

        var shortHeaders = plan
            .Select(p => p.DayIndex >= 0
                ? string.Empty // the start header is already short ("Старт (Д1)")
                : labels.ColumnHeadersShort is { } s && s.TryGetValue(p.Column, out var h) ? h : string.Empty)
            .ToList();

        var bodyWrap = plan.Select(p => ColumnBodyWraps(p.Column)).ToList();
        var shrinkPriority = plan.Select(p => ShrinkPriority(p.Column)).ToList();

        // The chip column's physical cell index (or -1 when hidden), so we can bold own-chip cells there.
        var chipIndex = plan.FindIndex(p => p.Column == StatementColumn.Chip);

        // Fixed statement order: chip-less last; rental chips before own chips; then by chip number ascending.
        var ordered = data.Rows
            .OrderBy(r => r.HasChip ? 0 : 1)
            .ThenBy(r => r.HasOwnChip ? 1 : 0)
            .ThenBy(r => r.ChipSortKey ?? int.MaxValue)
            .ThenBy(r => r.FullName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var body = new List<ResultProtocolBodyRow>(ordered.Count);
        var seq = 0;
        foreach (var row in ordered)
        {
            seq++;
            var cells = plan.Select(p => Cell(p.Column, row, seq, p.DayIndex)).ToList();

            // Bold only the chip cell, and only for an own chip (rental prints normal). Built lazily — most
            // rows are rental, so a null mask (all normal) is the common case.
            IReadOnlyList<bool>? bold = null;
            if (chipIndex >= 0 && row.HasOwnChip)
            {
                var mask = new bool[cells.Count];
                mask[chipIndex] = true;
                bold = mask;
            }

            body.Add(new ResultProtocolBodyRow(cells, BoldCells: bold));
        }

        var section = new ResultProtocolSection { GroupName = string.Empty, Rows = body };

        // The statement has NO header block (no competition name / title / date / venue) — only the applied-
        // filters heading over the table. So every header field is left blank; the renderers then print none.
        return new ResultProtocolDocument
        {
            Orientation = settings.Orientation,
            CompetitionName = string.Empty,
            Title = string.Empty,
            Subtitle = string.Empty,
            Venue = string.Empty,
            DateText = string.Empty,
            CompetitionType = string.Empty,
            FilterSummary = (filterSummary ?? string.Empty).Trim(),
            ColumnHeaders = headers,
            ColumnHeadersShort = shortHeaders,
            ColumnBodyWrap = bodyWrap,
            ColumnShrinkPriority = shrinkPriority,
            Sections = [section],
            Footer = ProtocolFooterFactory.Build(
                labels.FooterSoftwareName, labels.FooterGeneratedLabel, labels.FooterPageLabel)
        };
    }

    // Free-text columns (name, group, region, club, ДЮСШ, coach, team, representative, note) may wrap; the
    // short-code columns (№ з/п, number, birth date, chip, rank, ФСОУ) stay on one line.
    private static bool ColumnBodyWraps(StatementColumn column) => column switch
    {
        StatementColumn.FullName => true,
        StatementColumn.Group => true,
        StatementColumn.Region => true,
        StatementColumn.Club => true,
        StatementColumn.Dussh => true,
        StatementColumn.Coach => true,
        StatementColumn.Team => true,
        StatementColumn.Representative => true,
        StatementColumn.Note => true,
        _ => false
    };

    // How willingly a column gives up width when the table is too wide for the page (see
    // ResultProtocolDocument.ColumnShrinkPriority): 1 = never narrowed; 2/3/4 = shrink ever more willingly.
    // Keep the spine (№/number/name/birth/group/chip) protected; secondary identity columns give way first.
    private static int ShrinkPriority(StatementColumn column) => column switch
    {
        StatementColumn.Sequence => 1,
        StatementColumn.Number => 1,
        StatementColumn.BirthDate => 1,
        StatementColumn.Chip => 1,
        StatementColumn.Start => 1,
        StatementColumn.FullName => 2,
        StatementColumn.Group => 2,
        StatementColumn.Rank => 2,
        StatementColumn.FsouCode => 2,
        StatementColumn.Region => 3,
        StatementColumn.Club => 3,
        StatementColumn.Team => 3,
        StatementColumn.Dussh => 4,
        StatementColumn.Coach => 4,
        StatementColumn.Representative => 4,
        StatementColumn.Note => 4,
        _ => 1
    };

    private static string Cell(StatementColumn column, StatementRow row, int sequence, int dayIndex) => column switch
    {
        StatementColumn.Sequence => sequence.ToString(CultureInfo.InvariantCulture),
        StatementColumn.Number => row.Number,
        StatementColumn.FullName => row.FullName,
        StatementColumn.BirthDate => row.BirthDate is { } d ? d.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) : string.Empty,
        StatementColumn.Group => row.Group,
        StatementColumn.Chip => row.Chip,
        StatementColumn.Start => dayIndex >= 0 && dayIndex < row.StartTimes.Count ? row.StartTimes[dayIndex] : string.Empty,
        StatementColumn.Region => row.Region,
        StatementColumn.Club => row.Club,
        StatementColumn.Dussh => row.Dussh,
        StatementColumn.Coach => row.Coach,
        StatementColumn.Rank => row.Rank,
        StatementColumn.Team => row.Team,
        StatementColumn.Representative => row.Representative,
        StatementColumn.FsouCode => row.FsouCode,
        StatementColumn.Note => row.Note,
        _ => string.Empty
    };

    /// <summary>One physical output column: the logical <see cref="StatementColumn"/> it renders and, for a
    /// per-day «Старт» sub-column, which day it is (index into <see cref="StatementData.DayLabels"/> /
    /// <see cref="StatementRow.StartTimes"/>). <see cref="DayIndex"/> is -1 for every non-start column.</summary>
    private readonly record struct PlanColumn(StatementColumn Column, int DayIndex);
}
