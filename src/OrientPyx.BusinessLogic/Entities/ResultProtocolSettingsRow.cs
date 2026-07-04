namespace OrientPyx.BusinessLogic.Entities;

/// <summary>
/// Per-day results-protocol template, stored in the event database (one row per <see cref="EventDay"/>).
/// Holds the protocol layout (orientation, ordered/visible columns, header text) serialised as JSON — the
/// same shape the app-level default uses. A day with no row falls back to the app-level default at load
/// time, so a brand-new day starts from the configured template and is saved per day from then on.
/// </summary>
public class ResultProtocolSettingsRow
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The day this template belongs to.</summary>
    public Guid EventDayId { get; set; }

    /// <summary>The <see cref="OrientPyx.BusinessLogic.Models.ResultProtocolSettings"/> serialised as JSON.</summary>
    public string Json { get; set; } = string.Empty;
}
