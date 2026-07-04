namespace OrientPyx.BusinessLogic.Models;

/// <summary>How the multi-day summary («Підсумковий залік») totals a participant across the counted days.</summary>
public enum SummaryMode
{
    /// <summary>Sum the ranking points («Очки») earned on each counted day; higher total wins. The «Очки»
    /// per-day sub-column is shown.</summary>
    ByPoints,

    /// <summary>Sum the result times on each counted day; the smaller total wins. The «Очки» per-day
    /// sub-column is hidden (only place + time per day). A participant missing any counted result is
    /// always out of the ranking (поза заліком).</summary>
    ByTime
}

/// <summary>
/// One day's participation in the summary, in on-page order: which day it is and whether it is counted.
/// A hidden (unchecked) day keeps its place in the order so the selection can be toggled back on. The list
/// order is the printed left-to-right order of the per-day column bands.
/// </summary>
public sealed class SummaryDaySetting
{
    /// <summary>The day's id (matches <c>EventDay.Id</c>).</summary>
    public Guid DayId { get; set; }

    /// <summary>The day's 1-based number, kept so an old saved selection still labels the day if the day
    /// list changed.</summary>
    public int DayNumber { get; set; }

    /// <summary>Whether this day is included in the total (and shown as a column band).</summary>
    public bool Counted { get; set; } = true;
}

/// <summary>
/// One configurable leading column in the summary settings: which <see cref="SummaryColumn"/> it is and whether
/// it is currently shown. The list order IS the on-page order of the leading columns (the per-day result bands
/// and the trailing «Сума» always follow them). Persisted (JSON) inside <see cref="SummaryProtocolSettings"/>.
/// </summary>
public sealed class SummaryColumnSetting
{
    public SummaryColumn Column { get; set; }

    /// <summary>Whether this column is printed. A hidden column keeps its place in the order for later.</summary>
    public bool Visible { get; set; } = true;
}

/// <summary>
/// Competition-level configuration for the multi-day summary protocol («Протокол по сумі днів»): the summing
/// mode, which days count (and in what order), the tie-break priority day, and — for the points mode — whether
/// only participants with a result on every counted day are in the ranking. Persisted as JSON in the event
/// database (one row per competition, since the day set is competition-specific). The header text fields mirror
/// the results protocol's: blank ⇒ fall back to the competition metadata at build time.
/// </summary>
public sealed class SummaryProtocolSettings
{
    public SummaryMode Mode { get; set; } = SummaryMode.ByPoints;

    public ProtocolOrientation Orientation { get; set; } = ProtocolOrientation.Landscape;

    /// <summary>The leading (identity) columns in on-page order, before the per-day result bands. Reordered and
    /// toggled in the settings UI. Defaults to the printed summary sheet's layout (Місце, ПІБ, ДН, Регіон,
    /// Клуб).</summary>
    public List<SummaryColumnSetting> LeadingColumns { get; set; } = DefaultLeadingColumns();

    /// <summary>The days, in on-page order, with their counted flag. Reconciled against the live day list at
    /// load time (new days appended counted, vanished days dropped).</summary>
    public List<SummaryDaySetting> Days { get; set; } = [];

    /// <summary>The tie-break priority day: when two participants tie on the total, the one with the better
    /// result on this day wins. Null ⇒ the first counted day is used.</summary>
    public Guid? PriorityDayId { get; set; }

    /// <summary>Points mode only. When true, only participants with a counted result on EVERY counted day are
    /// ranked; the rest are listed поза конкурсом at the end of the group. When false, everyone is ranked —
    /// first by total points, then by the number of counted results (more first), then by the priority day.</summary>
    public bool RequireAllDays { get; set; }

    // ── Header text. Blank ⇒ fall back to the competition's own value at build time. ────────────────

    public string CompetitionName { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Subtitle { get; set; } = string.Empty;
    public string Venue { get; set; } = string.Empty;
    public string CompetitionType { get; set; } = string.Empty;
    public string DateText { get; set; } = string.Empty;

    /// <summary>The default leading-column layout for the summary sheet: place, full name, birth date, region,
    /// club (shown); the rest (bib number, ДЮСШ, coach, qualification) are available but hidden by default.</summary>
    public static List<SummaryColumnSetting> DefaultLeadingColumns() =>
    [
        new() { Column = SummaryColumn.Sequence, Visible = true },
        new() { Column = SummaryColumn.FullName, Visible = true },
        new() { Column = SummaryColumn.BirthDate, Visible = true },
        new() { Column = SummaryColumn.Region, Visible = true },
        new() { Column = SummaryColumn.Club, Visible = true },
        new() { Column = SummaryColumn.Number, Visible = false },
        new() { Column = SummaryColumn.Dussh, Visible = false },
        new() { Column = SummaryColumn.Coach, Visible = false },
        new() { Column = SummaryColumn.Rank, Visible = false },
    ];
}
