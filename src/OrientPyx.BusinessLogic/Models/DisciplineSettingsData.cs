using OrientPyx.BusinessLogic.Disciplines;

namespace OrientPyx.BusinessLogic.Models;

/// <summary>A discipline's settings for the settings dialog: the definitions and the effective value of each.</summary>
/// <param name="PluginId">The contributing plugin (its id prefixes the labels' string keys); null for a built-in.</param>
public sealed record DisciplineSettingsData(
    DisciplineId DisciplineId,
    string? PluginId,
    IReadOnlyList<SettingDef> Definitions,
    IReadOnlyDictionary<string, object?> Values);
