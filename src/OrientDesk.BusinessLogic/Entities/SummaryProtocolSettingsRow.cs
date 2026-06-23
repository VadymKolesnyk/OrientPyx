namespace OrientDesk.BusinessLogic.Entities;

/// <summary>
/// The competition-level multi-day summary-protocol template, stored in the event database as a single row.
/// Unlike the per-day results/start protocol templates, the summary spans every day (its day selection and
/// priority day are competition-specific), so there is exactly one row per competition. Holds the
/// <see cref="OrientDesk.BusinessLogic.Models.SummaryProtocolSettings"/> serialised as JSON.
/// </summary>
public class SummaryProtocolSettingsRow
{
    /// <summary>Singleton row — always 1.</summary>
    public int Id { get; set; } = 1;

    public string Json { get; set; } = string.Empty;
}
