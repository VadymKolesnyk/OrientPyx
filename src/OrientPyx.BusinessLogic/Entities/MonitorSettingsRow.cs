namespace OrientPyx.BusinessLogic.Entities;

/// <summary>
/// The competition-level on-screen-monitor settings, stored in the event database as a single row. Holds the
/// <see cref="OrientPyx.BusinessLogic.Models.MonitorSettings"/> (the list of output HTML files with their
/// group selection, columns and timing) serialised as JSON. There is exactly one row per competition.
/// </summary>
public class MonitorSettingsRow
{
    /// <summary>Singleton row — always 1.</summary>
    public int Id { get; set; } = 1;

    public string Json { get; set; } = string.Empty;
}
