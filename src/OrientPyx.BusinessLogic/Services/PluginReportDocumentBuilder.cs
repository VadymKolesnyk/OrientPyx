using System.Globalization;
using OrientPyx.BusinessLogic.Disciplines.Plugins;
using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Services;

/// <summary>The competition header a plugin report is printed under (as the protocols default it).</summary>
internal sealed record PluginReportHeader(string Organisation, string CompetitionName, string DateText, string Venue);

/// <summary>
/// Turns a plugin's <see cref="ReportDocument"/> into the application's <see cref="ResultProtocolDocument"/>, so a
/// report is shown by the protocol preview and written by the .docx protocol writer (Р-64): the competition header
/// above the report's title, the report's columns and sections, the officials and the page footer. Column labels are
/// the plugin's string keys (or text); cell values are formatted like the protocols' (a result time «h:mm:ss», a
/// status code, points). Pure — no UI, no files.
/// </summary>
internal static class PluginReportDocumentBuilder
{
    public static ResultProtocolDocument Build(
        ReportDocument report,
        PluginReportInfo info,
        PluginReportHeader header,
        ProtocolOfficialsData officials,
        PluginReportLabels labels)
    {
        var columns = report.Columns ?? [];
        string Label(string key) => labels.Text(PluginStrings.Key(info.PluginId, key));

        return new ResultProtocolDocument
        {
            Orientation = report.Landscape ? ProtocolOrientation.Landscape : ProtocolOrientation.Portrait,
            Subtitle = header.Organisation,
            CompetitionName = header.CompetitionName,
            Title = string.IsNullOrWhiteSpace(report.Title) ? labels.Text(info.NameKey) : report.Title.Trim(),
            DateText = header.DateText,
            Venue = header.Venue,
            // The report's own line under the header (centred, bold — the slot the statement's filter line uses).
            FilterSummary = (report.Subtitle ?? string.Empty).Trim(),
            ColumnHeaders = columns.Select(c => Label(c.Label)).ToList(),
            ColumnBodyWrap = columns.Select(c => c.Wrap).ToList(),
            // Free text gives up width first when the table is too wide; short values keep theirs.
            ColumnShrinkPriority = columns.Select(c => c.Wrap ? 3 : 1).ToList(),
            Sections = (report.Sections ?? []).Select(s => Section(s, columns.Count)).ToList(),
            Officials = report.Officials
                ? ProtocolOfficialsFactory.Build(officials, labels.ChiefJudgeLabel, labels.ChiefSecretaryLabel, labels.JuryLabel)
                : [],
            Footer = ProtocolFooterFactory.Build(labels.FooterSoftwareName, labels.FooterGeneratedLabel, labels.FooterPageLabel)
        };
    }

    private static ResultProtocolSection Section(ReportSection section, int columnCount) => new()
    {
        GroupName = (section.Title ?? string.Empty).Trim(),
        DistanceText = (section.Subtitle ?? string.Empty).Trim(),
        RankCalculationText = (section.Note ?? string.Empty).Trim(),
        Rows = (section.Rows ?? []).Where(r => r is not null).Select(r => Row(r, columnCount)).ToList()
    };

    // One cell per column: a short row is padded with blanks, extra cells are dropped.
    private static ResultProtocolBodyRow Row(ReportRow row, int columnCount)
    {
        var cells = new string[columnCount];
        for (var i = 0; i < columnCount; i++)
            cells[i] = i < row.Cells.Count ? Format(row.Cells[i]) : string.Empty;
        return new ResultProtocolBodyRow(
            cells,
            IsTeamHeader: row.Kind == ReportRowKind.Caption,
            TeamName: row.Kind == ReportRowKind.Caption ? cells.FirstOrDefault(c => c.Length > 0) ?? string.Empty : string.Empty,
            IsSubRow: row.Kind == ReportRowKind.Sub);
    }

    /// <summary>A report cell as printed: blank for null, a result time, a status code, points as the protocols
    /// print them, a date «dd.MM.yyyy», «✓» for true; anything else as text.</summary>
    public static string Format(object? value) => value switch
    {
        null => string.Empty,
        string s => s,
        TimeSpan t => TimeFormat.Result(t),
        FinishStatus status => StatusCode(status),
        decimal d => PointsTable.Format(d),
        double d when double.IsFinite(d) => PointsTable.Format((decimal)d),
        float f when float.IsFinite(f) => PointsTable.Format((decimal)f),
        bool b => b ? "✓" : string.Empty,
        DateOnly date => date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.CurrentCulture),
        _ => value.ToString() ?? string.Empty
    };

    // The protocols' language-neutral status codes; a blank status (not read out) prints nothing.
    private static string StatusCode(FinishStatus status) => status switch
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
