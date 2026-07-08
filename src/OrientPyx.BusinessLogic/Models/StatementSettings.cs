namespace OrientPyx.BusinessLogic.Models;

/// <summary>
/// One selectable column in the statement settings: which <see cref="StatementColumn"/> it is and whether
/// it is currently shown. The list order IS the on-page column order, so the settings UI reorders this list.
/// Persisted (JSON) per competition and as an app-level default via <see cref="StatementSettings"/>.
/// </summary>
public sealed class StatementColumnSetting
{
    public StatementColumn Column { get; set; }

    /// <summary>Whether this column is printed. A hidden column keeps its place in the order for later.</summary>
    public bool Visible { get; set; } = true;
}

/// <summary>
/// Configuration for the participant-statement («відомість») export/print: page orientation, the ordered set
/// of columns to show, and the editable header text (title, subtitle, competition name, venue, date). The
/// header fields default to the current competition's metadata but can be overridden here; a blank field falls
/// back to the competition value at build time. Persisted as JSON per competition (event DB) and as an
/// app-level default (app DB), mirroring <see cref="ResultProtocolSettings"/>.
/// </summary>
public sealed class StatementSettings
{
    public ProtocolOrientation Orientation { get; set; } = ProtocolOrientation.Portrait;

    /// <summary>The columns in on-page order. Defaults to a sensible identity layout.</summary>
    public List<StatementColumnSetting> Columns { get; set; } = DefaultColumns();

    // ── Header text. Blank ⇒ fall back to the competition's own value at build time. ────────────────

    /// <summary>Competition-name line, printed centred above the title. Blank ⇒ the current competition's name.</summary>
    public string CompetitionName { get; set; } = string.Empty;

    /// <summary>Main title line, e.g. "ВІДОМІСТЬ УЧАСНИКІВ". Blank ⇒ a localized default.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Sub-title under the main title (the organisation / club line). Blank ⇒ the competition organisation.</summary>
    public string Subtitle { get; set; } = string.Empty;

    /// <summary>Venue line ("м. Київ"). Blank ⇒ the competition venue.</summary>
    public string Venue { get; set; } = string.Empty;

    /// <summary>Date line ("31.05.2026"). Blank ⇒ the selected day's / competition's date.</summary>
    public string DateText { get; set; } = string.Empty;

    /// <summary>The default column layout for a participant statement: sequence, number, name, birth year, group,
    /// chip, region, club, ДЮСШ, coach, qualification. Representative / ФСОУ / note / team default hidden.</summary>
    public static List<StatementColumnSetting> DefaultColumns() =>
    [
        new() { Column = StatementColumn.Sequence, Visible = true },
        new() { Column = StatementColumn.Number, Visible = true },
        new() { Column = StatementColumn.FullName, Visible = true },
        new() { Column = StatementColumn.BirthDate, Visible = true },
        new() { Column = StatementColumn.Group, Visible = true },
        new() { Column = StatementColumn.Chip, Visible = true },
        new() { Column = StatementColumn.Start, Visible = false },
        new() { Column = StatementColumn.Region, Visible = true },
        new() { Column = StatementColumn.Club, Visible = true },
        new() { Column = StatementColumn.Dussh, Visible = true },
        new() { Column = StatementColumn.Coach, Visible = true },
        new() { Column = StatementColumn.Rank, Visible = true },
        new() { Column = StatementColumn.Team, Visible = false },
        new() { Column = StatementColumn.Representative, Visible = false },
        new() { Column = StatementColumn.FsouCode, Visible = false },
        new() { Column = StatementColumn.Note, Visible = false },
    ];
}
