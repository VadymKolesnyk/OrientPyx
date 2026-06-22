namespace OrientDesk.BusinessLogic.Entities;

/// <summary>
/// Single-row table in the app database holding configurable paths.
/// Paths may be relative to the application directory (defaults: ./data, ./events).
/// </summary>
public class AppSettingsRow
{
    /// <summary>Fixed primary key — there is only ever one settings row.</summary>
    public int Id { get; set; } = 1;

    public string EventsPath { get; set; } = string.Empty;

    /// <summary>UI font scale multiplier (1.0 = default). Applied across the whole app.</summary>
    public double FontScale { get; set; } = 1.0;

    /// <summary>Installed Windows printer used for split printouts; blank until the user picks one.</summary>
    public string PrinterName { get; set; } = string.Empty;

    /// <summary>Thermal-roll width in millimetres for split printouts (56 or 80; default 80).</summary>
    public int ReceiptWidthMm { get; set; } = 80;

    /// <summary>
    /// The results-protocol settings (orientation, column set + order, header text) serialised as JSON.
    /// Blank until the user saves a configuration; the caller then applies the defaults. Stored at the
    /// application level so one protocol layout is shared across competitions.
    /// </summary>
    public string ResultProtocolJson { get; set; } = string.Empty;

    /// <summary>
    /// App-level default template for the <b>regular</b> start protocol, serialised as JSON. Blank until the
    /// user saves one (via "save for next competitions"); a new day seeds from this, falling back to the
    /// built-in kind default when blank. Stored per kind so each start protocol keeps its own default layout.
    /// </summary>
    public string StartProtocolRegularJson { get; set; } = string.Empty;

    /// <summary>App-level default template for the <b>judges'</b> start protocol, serialised as JSON (see
    /// <see cref="StartProtocolRegularJson"/>).</summary>
    public string StartProtocolJudgesJson { get; set; } = string.Empty;

    /// <summary>
    /// Мінімум учасників у групі для дійсності присвоєння будь-якого розряду (Додаток 89, п.7 — «не менше
    /// трьох»): a group with fewer participants awards no ranks at all. Default 3.
    /// </summary>
    public int RankMinParticipants { get; set; } = 3;

    /// <summary>
    /// Minimum number of distinct regions across the whole competition (day) for any rank to be valid
    /// (Додаток 89, п.51 — «не менше восьми областей» for individual events). Default 8.
    /// </summary>
    public int RankMinRegions { get; set; } = 8;

    /// <summary>
    /// How many of the group's highest-ranked participants are summed to compute the group's course rank
    /// («Ранг змагань») — only the first N current-rank point values count (Додаток 89). Default 12.
    /// </summary>
    public int RankCountForRank { get; set; } = 12;
}
