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
}
