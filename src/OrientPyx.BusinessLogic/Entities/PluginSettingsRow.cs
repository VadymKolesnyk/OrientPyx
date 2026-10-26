using OrientPyx.BusinessLogic.Disciplines;

namespace OrientPyx.BusinessLogic.Entities;

/// <summary>
/// Saved settings of one (plugin) discipline, as a JSON object keyed by setting key (Р-11). A row with an
/// <see cref="EventDayId"/> holds the discipline's day-scoped values for that day; the row with no day holds its
/// competition-scoped values. At most one row per (day, discipline).
/// </summary>
public class PluginSettingsRow
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The day the values belong to; null for the competition-scoped row.</summary>
    public Guid? EventDayId { get; set; }

    public DisciplineId DisciplineId { get; set; }

    public string Json { get; set; } = string.Empty;
}
