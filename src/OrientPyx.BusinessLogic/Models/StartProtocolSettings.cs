namespace OrientPyx.BusinessLogic.Models;

/// <summary>
/// One selectable column in the start-protocol settings: which <see cref="StartProtocolColumn"/> it is and
/// whether it is shown. The list order IS the on-page column order. Persisted (JSON) per day.
/// </summary>
public sealed class StartProtocolColumnSetting
{
    public StartProtocolColumn Column { get; set; }

    /// <summary>Whether this column is printed. A hidden column keeps its place in the order for later.</summary>
    public bool Visible { get; set; } = true;
}

/// <summary>
/// Per-day configuration for a start-protocol export: page orientation, the ordered set of columns to show,
/// and the editable header text (title, subtitle, type, venue, date). Header fields default to the current
/// competition's metadata but can be overridden here. Persisted as JSON in the event database, per day and
/// per <see cref="StartProtocolKind"/> (the regular and judges' protocols each keep their own template).
/// </summary>
public sealed class StartProtocolSettings
{
    public ProtocolOrientation Orientation { get; set; } = ProtocolOrientation.Portrait;

    /// <summary>The columns in on-page order. Defaults differ by kind (see the factory methods below).</summary>
    public List<StartProtocolColumnSetting> Columns { get; set; } = RegularDefaultColumns();

    // ── Header text. Blank ⇒ fall back to the competition's own value at build time. ────────────────

    /// <summary>Competition-name line, printed centred above the title. Blank ⇒ the current competition's name.</summary>
    public string CompetitionName { get; set; } = string.Empty;

    /// <summary>Main title line. Blank ⇒ a localized default ("СТАРТОВИЙ ПРОТОКОЛ").</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Sub-title under the title (organisation / club line). Blank ⇒ the competition organisation.</summary>
    public string Subtitle { get; set; } = string.Empty;

    /// <summary>Venue line ("м. Київ"). Blank ⇒ the competition venue.</summary>
    public string Venue { get; set; } = string.Empty;

    /// <summary>Competition-type line, printed centred on the date/venue row. Free text; blank ⇒ nothing centred.</summary>
    public string CompetitionType { get; set; } = string.Empty;

    /// <summary>Date line ("31.05.2026"). Blank ⇒ the selected day's date.</summary>
    public string DateText { get; set; } = string.Empty;

    /// <summary>Default column layout for the regular (by-group) start protocol: Старт, №, ПІБ, рік, клуб,
    /// регіон, чіп — with sequence/qualification/ДЮСШ/тренер/група available but hidden.</summary>
    public static List<StartProtocolColumnSetting> RegularDefaultColumns() =>
    [
        new() { Column = StartProtocolColumn.StartTime, Visible = true },
        new() { Column = StartProtocolColumn.Number, Visible = true },
        new() { Column = StartProtocolColumn.FullName, Visible = true },
        new() { Column = StartProtocolColumn.BirthDate, Visible = true },
        new() { Column = StartProtocolColumn.Club, Visible = true },
        new() { Column = StartProtocolColumn.Region, Visible = true },
        new() { Column = StartProtocolColumn.Chip, Visible = true },
        new() { Column = StartProtocolColumn.Sequence, Visible = false },
        new() { Column = StartProtocolColumn.Rank, Visible = false },
        new() { Column = StartProtocolColumn.Dussh, Visible = false },
        new() { Column = StartProtocolColumn.Coach, Visible = false },
        new() { Column = StartProtocolColumn.Group, Visible = false },
        new() { Column = StartProtocolColumn.Team, Visible = false },
        new() { Column = StartProtocolColumn.Note, Visible = false },
    ];

    /// <summary>Default column layout for the judges' (by-minute) protocol — the compact «суддівський» sheet
    /// (one minute caption row, then a tight table): № з/п, Номер, Чіп, Група, ПІБ, команда, Прим. The start
    /// time itself is the minute caption row, so the «Старт» column is hidden by default.</summary>
    public static List<StartProtocolColumnSetting> JudgesDefaultColumns() =>
    [
        new() { Column = StartProtocolColumn.Sequence, Visible = true },
        new() { Column = StartProtocolColumn.Number, Visible = true },
        new() { Column = StartProtocolColumn.Chip, Visible = true },
        new() { Column = StartProtocolColumn.Group, Visible = true },
        new() { Column = StartProtocolColumn.FullName, Visible = true },
        new() { Column = StartProtocolColumn.Team, Visible = true },
        new() { Column = StartProtocolColumn.Note, Visible = true },
        new() { Column = StartProtocolColumn.StartTime, Visible = false },
        new() { Column = StartProtocolColumn.BirthDate, Visible = false },
        new() { Column = StartProtocolColumn.Club, Visible = false },
        new() { Column = StartProtocolColumn.Region, Visible = false },
        new() { Column = StartProtocolColumn.Rank, Visible = false },
        new() { Column = StartProtocolColumn.Dussh, Visible = false },
        new() { Column = StartProtocolColumn.Coach, Visible = false },
    ];

    /// <summary>The default settings for a kind (used when a day has no saved template).</summary>
    public static StartProtocolSettings Default(StartProtocolKind kind) => new()
    {
        Columns = kind == StartProtocolKind.Judges ? JudgesDefaultColumns() : RegularDefaultColumns()
    };
}
