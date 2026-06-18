namespace OrientDesk.BusinessLogic.Models;

/// <summary>Page orientation for the generated results protocol.</summary>
public enum ProtocolOrientation
{
    /// <summary>Portrait (default) — taller than wide.</summary>
    Portrait,

    /// <summary>Landscape — wider than tall, for protocols with many columns.</summary>
    Landscape
}

/// <summary>
/// One selectable column in the protocol settings: which <see cref="ProtocolColumn"/> it is and whether
/// it is currently shown. The list order IS the on-page column order, so the settings UI reorders this
/// list. Persisted (JSON) at the application level via <see cref="ResultProtocolSettings"/>.
/// </summary>
public sealed class ProtocolColumnSetting
{
    public ProtocolColumn Column { get; set; }

    /// <summary>Whether this column is printed. A hidden column keeps its place in the order for later.</summary>
    public bool Visible { get; set; } = true;
}

/// <summary>
/// Application-level configuration for the results protocol export: page orientation, the ordered set of
/// columns to show, and the editable header text (title, subtitle, venue, date). The header fields default
/// to the current competition's metadata but can be overridden here; a blank field falls back to the
/// competition value at build time. Persisted as JSON in the app database (shared across competitions).
/// </summary>
public sealed class ResultProtocolSettings
{
    public ProtocolOrientation Orientation { get; set; } = ProtocolOrientation.Portrait;

    /// <summary>The columns in on-page order. Defaults to a sensible personal-protocol layout.</summary>
    public List<ProtocolColumnSetting> Columns { get; set; } = DefaultColumns();

    // ── Header text. Blank ⇒ fall back to the competition's own value at build time. ────────────────

    /// <summary>Main title line, e.g. "ПРОТОКОЛ РЕЗУЛЬТАТІВ ЗМАГАНЬ З ОРІЄНТУВАННЯ". Blank ⇒ a localized default.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Sub-title under the main title (the organisation / club line). Blank ⇒ the competition organisation.</summary>
    public string Subtitle { get; set; } = string.Empty;

    /// <summary>Venue line ("м. Київ"). Blank ⇒ the competition venue.</summary>
    public string Venue { get; set; } = string.Empty;

    /// <summary>Competition-type line, printed centred on the date/venue row ("середня дистанція у
    /// заданому напрямку"). Free text; blank ⇒ nothing is printed in the centre of that row.</summary>
    public string CompetitionType { get; set; } = string.Empty;

    /// <summary>Date line ("31.05.2026"). Blank ⇒ the selected day's date.</summary>
    public string DateText { get; set; } = string.Empty;

    /// <summary>The default column layout: a personal results protocol (№, name, birth, club, ДЮСШ, coach,
    /// result, place, qualification, score). Score is shown for scored disciplines and blank otherwise.</summary>
    public static List<ProtocolColumnSetting> DefaultColumns() =>
    [
        new() { Column = ProtocolColumn.Sequence, Visible = true },
        new() { Column = ProtocolColumn.FullName, Visible = true },
        new() { Column = ProtocolColumn.BirthDate, Visible = true },
        new() { Column = ProtocolColumn.Club, Visible = true },
        new() { Column = ProtocolColumn.Dussh, Visible = true },
        new() { Column = ProtocolColumn.Coach, Visible = true },
        new() { Column = ProtocolColumn.Region, Visible = true },
        new() { Column = ProtocolColumn.Result, Visible = true },
        new() { Column = ProtocolColumn.Place, Visible = true },
        new() { Column = ProtocolColumn.Rank, Visible = true },
        new() { Column = ProtocolColumn.Score, Visible = true },
        new() { Column = ProtocolColumn.Number, Visible = false },
    ];
}
