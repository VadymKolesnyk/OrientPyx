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
    bool Enabled)
{
    /// <summary>Defaults for a competition that has never been configured: slug from its identifier, title
    /// from its name, subtitle from its date range; standings/points off, not enabled.</summary>
    public static OnlinePublishSettings Default(string slug, string title, string subtitle) =>
        new(slug, title, subtitle, Standings: false, Points: false, Enabled: false);
}
