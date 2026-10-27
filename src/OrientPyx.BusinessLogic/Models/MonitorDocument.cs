namespace OrientPyx.BusinessLogic.Models;

/// <summary>
/// The fully-resolved, values-only content of one monitor HTML file: the page title, its timing
/// (auto-refresh / auto-scroll), the ordered columns to render, and one section per chosen group with its
/// already-formatted result rows. Built by the editor service from the day's computed snapshot + a
/// <see cref="MonitorFile"/>; turned into a self-contained HTML page by <c>IMonitorHtmlWriter</c>
/// (DataAccess). Layer-neutral: no EF Core, no HTML — just the shape the writer renders.
/// </summary>
public sealed record MonitorDocument(
    string Title,
    string Subtitle,
    int RefreshSeconds,
    int ScrollSpeed,
    IReadOnlyList<MonitorColumn> Columns,
    IReadOnlyList<MonitorGroup> Groups);

/// <summary>A built monitor document paired with the absolute file path it should be written to.</summary>
public sealed record MonitorFileDocument(string Path, MonitorDocument Document);

/// <summary>
/// The day's computed inputs the monitor preview builds a document from — the result data and the resolved
/// page subtitle (competition name). Loaded once per day and cached by the configuration page so it can
/// re-render the live preview on every column/group edit without re-reading the database.
/// </summary>
public sealed record MonitorPreviewSource(ResultProtocolData Data, string Subtitle);

/// <summary>
/// Localized labels the monitor build needs, gathered once by the page so the editor service (and the HTML
/// writer) stay free of <c>ILocalizationService</c>: the header for every result column (keyed by
/// <see cref="ResultColumn"/>), the short status codes for unplaced runs, the course-distance / control-count
/// caption templates, and the "updated at" footer template ("{0}" = time).
/// </summary>
public sealed record MonitorLabels(
    IReadOnlyDictionary<ResultColumn, string> ColumnHeaders,
    string DistanceLabel,
    string ControlCountLabel,
    string GeneratedLabel,
    string StatusDns,
    string StatusMp,
    string StatusOvt,
    string StatusDnf,
    string StatusDsq,
    string StatusRunning,
    /// <summary>Resolves an extra column's label key (§10.2); null ⇒ the key is shown as is.</summary>
    Func<string, string>? Translate = null);

/// <summary>One rendered column: its result-column id (for cell alignment hints) and its header label.</summary>
public sealed record MonitorColumn(ResultColumn Column, string Header)
{
    /// <summary>The column's stable key (the built-in key, or the extra key for <see cref="ResultColumn.Extra"/>).</summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>For <see cref="ResultColumn.Extra"/>: the extra column the cells come from.</summary>
    public ExtraResultColumn? Extra { get; init; }
}

/// <summary>One group section on a monitor page: the group caption and its ordered rows.</summary>
public sealed record MonitorGroup(string Name, string Caption, IReadOnlyList<MonitorRow> Cells);

/// <summary>
/// One rendered result row: the cell text for each column of the page (parallel to
/// <see cref="MonitorDocument.Columns"/>) plus a flag marking an unplaced (DNS/MP/…) runner so the writer
/// can grey the row out, mirroring the legacy monitor's shaded DNS rows.
/// </summary>
public sealed record MonitorRow(IReadOnlyList<string> Values, bool Unplaced)
{
    /// <summary>A relay group's team row or one of its leg rows (§11.4); <see cref="MonitorRowKind.Runner"/> otherwise.</summary>
    public MonitorRowKind Kind { get; init; } = MonitorRowKind.Runner;
}

/// <summary>What a monitor row stands for: a runner (personal), or — in a relay group — a team and its legs under it.</summary>
public enum MonitorRowKind
{
    Runner,
    Team,
    Leg,
}
