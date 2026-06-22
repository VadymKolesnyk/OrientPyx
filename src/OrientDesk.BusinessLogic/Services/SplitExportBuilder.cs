using OrientDesk.BusinessLogic.Enums;
using OrientDesk.BusinessLogic.Interfaces;
using OrientDesk.BusinessLogic.Models;

namespace OrientDesk.BusinessLogic.Services;

/// <summary>
/// Default <see cref="ISplitExportBuilder"/>. Resolves the header (the caller has already folded the
/// competition fallbacks into it), formats each runner's result/place/status, and orders every group's rows
/// placed finishers first (by place, then by name) with the rest after. Mirrors <see cref="ResultProtocolBuilder"/>;
/// the per-control split times come straight from each row's <see cref="SplitsView"/>, untouched here.
/// </summary>
public sealed class SplitExportBuilder : ISplitExportBuilder
{
    public SplitExportDocument Build(SplitExportData data, SplitExportHeader header, SplitExportLabels labels)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(labels);

        var groups = new List<SplitExportGroup>(data.Groups.Count);
        foreach (var g in data.Groups.OrderBy(g => g.Order))
        {
            var rows = g.Rows
                // Placed finishers first by ascending place, then the rest by name (the classic order).
                .OrderBy(r => r.Result.Place is { } p ? p : int.MaxValue)
                .ThenBy(r => r.FullName, StringComparer.CurrentCultureIgnoreCase)
                .Select(r => BuildRow(r, labels))
                .ToList();

            groups.Add(new SplitExportGroup(
                g.Name, g.Layout, g.DistanceKm, g.ControlCount, g.Controls, g.HasPoints, rows));
        }

        var title = header.Title.Trim().Length > 0 ? header.Title.Trim() : labels.DefaultTitle;

        return new SplitExportDocument(
            Title: title,
            Subtitle: header.Subtitle.Trim(),
            CompetitionType: header.CompetitionType.Trim(),
            Venue: header.Venue.Trim(),
            DateText: header.DateText.Trim(),
            Groups: groups,
            Labels: labels);
    }

    private static SplitExportRow BuildRow(SplitExportDataRow row, SplitExportLabels labels)
    {
        var r = row.Result;
        var ok = r.Status == FinishStatus.Ok;

        // The result column: a score for a point format, else the result time for an OK run; blank otherwise.
        var resultText = ok
            ? (r.Score is { } s ? s.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : r.ResultTime is { } e && e >= TimeSpan.Zero ? e.ToString("h\\:mm\\:ss") : string.Empty)
            : string.Empty;

        var placeText = ok && r.Place is { } p
            ? p.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : r.OutOfCompetition ? ParticipantDayResult.OutOfCompetitionMark
            : string.Empty;

        var statusText = ok ? string.Empty : ShortCode(r.Status);

        // For an OK scored result, spell the penalty/bonus under the total and carry the full breakdown
        // tooltip — both empty for a time result or a non-OK status (nothing to net out).
        var resultDetail = ok ? ScoreDetail(r) : string.Empty;
        var resultTooltip = ok ? ScoreTooltip(r, labels) : string.Empty;

        return new SplitExportRow(
            row.Number, row.FullName, row.Team, resultText, placeText, statusText, ok,
            resultDetail, resultTooltip, row.Splits);
    }

    // The penalty/bonus detail shown under the «Бали» total: a signed "−Y" penalty and/or "+B" bonus, joined
    // with a thin space; empty when neither applied (or the result isn't a score). Lets the cell make clear
    // the total already nets these out, the way the participant tables do.
    private static string ScoreDetail(ParticipantDayResult r)
    {
        if (r.Score is null)
            return string.Empty;
        var parts = new List<string>(2);
        if (r.ScorePenalty is { } pen && pen > 0)
            parts.Add("−" + pen.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (r.Bonus is { } bonus && bonus != 0)
            parts.Add(bonus.ToString("+0;-0", System.Globalization.CultureInfo.InvariantCulture));
        return string.Join(" ", parts);
    }

    // The «Бали» breakdown shown as the cell title — identical to the participant table's score tooltip
    // (per-control points, then gross/penalty/bonus and the net total). Empty when there is no scored
    // breakdown, so the writer emits no title attribute. Mirrors ResultText.ScoreTooltip.
    private static string ScoreTooltip(ParticipantDayResult r, SplitExportLabels labels)
    {
        if (r.Score is not { } total || r.ScoreBreakdown.Count == 0)
            return string.Empty;

        var ci = System.Globalization.CultureInfo.InvariantCulture;
        var sb = new System.Text.StringBuilder();
        sb.Append(labels.ScoreTooltipHeader);
        foreach (var line in r.ScoreBreakdown)
            sb.Append('\n').Append(string.Format(labels.ScoreTooltipControl, line.Code, line.Points));

        if (r.ScoreGross is { } gross && r.ScorePenalty is { } penalty && penalty > 0)
        {
            sb.Append('\n').Append(string.Format(labels.ScoreTooltipGross, gross));
            sb.Append('\n').Append(string.Format(labels.ScoreTooltipPenalty, penalty));
        }
        if (r.Bonus is { } bonus && bonus != 0)
            sb.Append('\n').Append(string.Format(labels.ScoreTooltipBonus, bonus.ToString("+0;-0", ci)));
        sb.Append('\n').Append(string.Format(labels.ScoreTooltipTotal, total));
        return sb.ToString();
    }

    // The standard language-neutral competition status code (mirrors ResultProtocolBuilder.ShortCode).
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
}
