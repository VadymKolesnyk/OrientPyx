namespace OrientPyx.BusinessLogic.Models;

/// <summary>
/// A ready-to-render multi-day summary protocol: a page header, then one section per group. Unlike the
/// single-tier results protocol, the table has a <b>two-tier banded header</b> — a set of leading identity
/// columns (№, ПІБ, ДН, Регіон, Клуб), then one band per counted day ("День 1 (30 травня)") spanning that
/// day's sub-columns (М / Час [ / Очки]), then a trailing «Сума» column. All values are pre-formatted strings;
/// the writer only lays them out. Built in BusinessLogic; rendered to .docx in DataAccess.
/// </summary>
public sealed class SummaryProtocolDocument
{
    public ProtocolOrientation Orientation { get; init; } = ProtocolOrientation.Landscape;

    public string CompetitionName { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Subtitle { get; init; } = string.Empty;
    public string Venue { get; init; } = string.Empty;
    public string DateText { get; init; } = string.Empty;
    public string CompetitionType { get; init; } = string.Empty;

    /// <summary>The leading identity columns (their captions), in on-page order — before the day bands.</summary>
    public IReadOnlyList<SummaryColumnSpec> LeadingColumns { get; init; } = [];

    /// <summary>The leaf-column index of the wide, wrapping name column (it left-aligns, wraps, and absorbs the
    /// table's slack width). -1 when the name column is hidden, so nothing is treated as the wide column.</summary>
    public int NameColumnIndex { get; init; } = -1;

    /// <summary>The day bands, in on-page order — each a band caption over its per-day sub-columns.</summary>
    public IReadOnlyList<SummaryDayBand> DayBands { get; init; } = [];

    /// <summary>The trailing total column caption ("Сума").</summary>
    public string TotalColumnHeader { get; init; } = string.Empty;

    /// <summary>Per <b>leaf</b> column (leading + day sub-columns + total, left to right), whether the body text
    /// wraps. Wrapping (free-text) columns are sized to a typical value and let long outliers wrap; non-wrapping
    /// (short-code) columns are sized to fit their longest value on one line. Used by the width algorithm.</summary>
    public IReadOnlyList<bool> ColumnBodyWrap { get; init; } = [];

    /// <summary>Per <b>leaf</b> column, the shrink priority used when the table overflows the page: 1 = never
    /// narrowed (protected); 4 = yields first and furthest. Mirrors the results protocol's priorities so the
    /// summary squeezes the same kinds of columns (the name column is protected; codes give way first).</summary>
    public IReadOnlyList<int> ColumnShrinkPriority { get; init; } = [];

    /// <summary>The group sections, in display order.</summary>
    public IReadOnlyList<SummaryProtocolSection> Sections { get; init; } = [];

    /// <summary>The officials' signature block printed at the end. Empty when none configured.</summary>
    public IReadOnlyList<ProtocolOfficial> Officials { get; init; } = [];

    /// <summary>The page footer; null ⇒ none printed.</summary>
    public ProtocolFooter? Footer { get; init; }

    /// <summary>The total number of leaf (data) columns: leading + (sub-columns × day bands) + 1 (total).</summary>
    public int LeafColumnCount =>
        LeadingColumns.Count + DayBands.Sum(b => b.SubColumns.Count) + 1;
}

/// <summary>One leading identity column: a stable key (its <see cref="SummaryColumn"/> name, used by the
/// preview's drag-reorder), a caption, and whether its body text may wrap (free-text columns).</summary>
public sealed record SummaryColumnSpec(string Key, string Caption, bool BodyWraps);

/// <summary>
/// One day band in the two-tier header: a band caption ("День 1 (30 травня)") spanning its per-day sub-columns
/// (М / Час, plus Очки in points mode). The sub-column captions are short codes shown on the lower header tier.
/// </summary>
public sealed record SummaryDayBand(string Caption, IReadOnlyList<string> SubColumns);

/// <summary>One group's block: a group caption and the ordered participant rows.</summary>
public sealed class SummaryProtocolSection
{
    public string GroupName { get; init; } = string.Empty;

    /// <summary>The data rows; each is the ordered cell strings matching the flat leaf-column layout (leading
    /// columns, then each day band's sub-columns left-to-right, then the total column).</summary>
    public IReadOnlyList<IReadOnlyList<string>> Rows { get; init; } = [];
}
