namespace OrientDesk.BusinessLogic.Entities;

/// <summary>
/// The competition-level online-publish settings, stored in the event database as a single row. Holds the
/// <see cref="OrientDesk.BusinessLogic.Models.OnlinePublishSettings"/> (slug, displayed title/subtitle,
/// standings / points flags, enabled) serialised as JSON. There is exactly one row per competition; the
/// connection keys are app-level (app.db), not here.
/// </summary>
public class OnlinePublishSettingsRow
{
    /// <summary>Singleton row — always 1.</summary>
    public int Id { get; set; } = 1;

    public string Json { get; set; } = string.Empty;
}
