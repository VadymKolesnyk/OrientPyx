namespace OrientPyx.BusinessLogic.Entities;

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

    /// <summary>Installed Windows printer used for A4 printing (the participant statement / «відомість»); blank
    /// until the user picks one. Kept separate from <see cref="PrinterName"/> so the thermal and A4 targets
    /// don't share a printer choice.</summary>
    public string A4PrinterName { get; set; } = string.Empty;

    /// <summary>The app-level default participant-statement («відомість») settings (orientation, column set +
    /// order, header text) serialised as JSON. Blank until the user saves one via "save for next competitions";
    /// a fresh competition seeds from this. Stored at the application level so one layout is shared.</summary>
    public string StatementJson { get; set; } = string.Empty;

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
    /// App-level default template for the multi-day summary protocol («підсумковий залік»), serialised as JSON.
    /// Blank until the user saves one (via "save for next competitions"); a competition with no template of its
    /// own seeds from this, falling back to the built-in defaults when blank. The per-competition day list and
    /// priority day are NOT carried over — they only make sense inside one competition.
    /// </summary>
    public string SummaryProtocolJson { get; set; } = string.Empty;

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

    // --- Online live-results (Supabase) connection, shared across competitions

    /// <summary>The Supabase project URL the live-results publisher pushes to. Blank until configured.</summary>
    public string OnlineSupabaseUrl { get; set; } = string.Empty;

    /// <summary>The Supabase <b>service-role</b> (write) key. Secret — kept only in this local database, never
    /// shared with the spectator frontend (which uses the public anon key). Blank until configured.</summary>
    public string OnlineServiceRoleKey { get; set; } = string.Empty;

    /// <summary>The public base URL of the spectator frontend (GitHub Pages), used to print shareable
    /// per-day links. Blank until configured.</summary>
    public string OnlinePublicBaseUrl { get; set; } = string.Empty;

    /// <summary>Seconds between live-results publish ticks. Default 10.</summary>
    public int OnlineIntervalSeconds { get; set; } = 10;

    /// <summary>
    /// Which timing-system readout format the app reads (0 = SPORTident, 1 = Sport Time). Application-level
    /// because the whole installation uses one timing system; picks the matching readout parser. Default
    /// SPORTident (0).
    /// </summary>
    public int ReadoutType { get; set; } = 0;

    /// <summary>Ids of the plugins the user switched on («Плагіни»), as a JSON string array. Blank = none: a plugin
    /// found in the plugin folders stays off until it is switched on.</summary>
    public string EnabledPluginsJson { get; set; } = string.Empty;

    /// <summary>
    /// Application-default CSV/Excel column mappings, as a JSON object keyed by header-set hash (each value maps an
    /// import target key to a header caption). Seeds a competition that has no mapping of its own for that file
    /// layout. Blank = none.
    /// </summary>
    public string ImportMappingsJson { get; set; } = string.Empty;

    /// <summary>
    /// UI language culture name (e.g. "uk-UA", "en-US") the app restores on startup. Blank until the user
    /// picks a language on the Settings page; when blank the built-in default (uk-UA) is used.
    /// </summary>
    public string Language { get; set; } = string.Empty;

    // --- Network mode («Спільна робота»): this machine as a host, and as a station

    /// <summary>Start serving stations on launch (the host switch was on when the app last ran).</summary>
    public bool HostEnabled { get; set; }

    /// <summary>The TCP port the host listens on; 0 ⇒ the default port (next free one when taken).</summary>
    public int HostPort { get; set; }

    /// <summary>The host name stations see; blank ⇒ the machine name.</summary>
    public string HostName { get; set; } = string.Empty;

    /// <summary>Generated once; identifies this host to its stations across restarts.</summary>
    public string HostId { get; set; } = string.Empty;

    /// <summary>This machine's name as a station; blank ⇒ the machine name.</summary>
    public string StationName { get; set; } = string.Empty;

    /// <summary>The last host this station connected to (<c>ip:port</c>); blank ⇒ none.</summary>
    public string StationHost { get; set; } = string.Empty;

    /// <summary>The session token that host issued; blank ⇒ a PIN is needed again.</summary>
    public string StationToken { get; set; } = string.Empty;

    /// <summary>The id of that host (a restored session is resumed only on the same host).</summary>
    public string StationHostId { get; set; } = string.Empty;

    /// <summary>This machine's stable device id as a station (generated once; hosts trust it by this).</summary>
    public string StationDeviceId { get; set; } = string.Empty;

    /// <summary>The PIN last used for the host in <see cref="StationHostId"/>; re-sent on the next connect.</summary>
    public string StationPin { get; set; } = string.Empty;
}
