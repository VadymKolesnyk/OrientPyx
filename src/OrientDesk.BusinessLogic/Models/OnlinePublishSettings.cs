namespace OrientDesk.BusinessLogic.Models;

/// <summary>
/// Per-competition online-publish options, stored as a single row in the event database. Drives how this
/// competition appears on the spectator frontend: its URL slug, the displayed title/subtitle, and whether
/// the multi-day standings («Сума») tab and the «Бали» column are shown. <see cref="Enabled"/> records the
/// user's intent to publish (it does not by itself run the background process — the page's Start button does
/// that). The connection keys are app-level (see <see cref="OnlineApiSettings"/>), not here.
/// </summary>
public sealed record OnlinePublishSettings(
    string Slug,
    string Title,
    string Subtitle,
    bool Standings,
    bool Points,
    bool Enabled,
    /// <summary>Legacy flat column selection (order + visible set, no per-screen distinction). Kept only so a
    /// config saved before <see cref="Display"/> existed still resolves — new saves leave this null and write
    /// <see cref="Display"/> instead.</summary>
    ResultColumnSelection? Columns = null,
    /// <summary>The rich online column layout: per-column large/small-screen visibility + the DSQ-column split.
    /// Sent to the frontend as the events row's <c>display_config</c>. Null means fall back to
    /// <see cref="OnlineDisplayConfig.Default"/> (or a legacy <see cref="Columns"/> when present).</summary>
    OnlineDisplayConfig? Display = null)
{
    /// <summary>The effective online layout: the saved <see cref="Display"/>, else a legacy flat
    /// <see cref="Columns"/> promoted to all-screens-visible, else the shared default.</summary>
    public OnlineDisplayConfig EffectiveDisplay =>
        Display is { } d ? d
        : Columns is { } c && c.HasAny ? OnlineDisplayConfig.FromSelection(c)
        : OnlineDisplayConfig.Default;

    /// <summary>Defaults for a competition that has never been configured: slug from its identifier, title
    /// from its name, subtitle from its date range; standings/points off, not enabled, default columns.</summary>
    public static OnlinePublishSettings Default(string slug, string title, string subtitle) =>
        new(slug, title, subtitle, Standings: false, Points: false, Enabled: false,
            Columns: null, Display: OnlineDisplayConfig.Default);
}
