using OrientPyx.BusinessLogic.Interfaces;

namespace OrientPyx.BusinessLogic.Disciplines.Plugins;

/// <summary>
/// A switched-on plugin's report (<see cref="Report"/>) as the application lists it: its identity and what it
/// declares, read once (guarded — a report whose <c>Id</c> / <c>Name</c> throws is left out). <see cref="NameKey"/>
/// and <see cref="DescriptionKey"/> are application string keys (<see cref="PluginStrings.Key"/>), resolved like the
/// plugin's other labels.
/// </summary>
public sealed record PluginReportInfo(
    string PluginId,
    string PluginName,
    string Id,
    string NameKey,
    string? DescriptionKey,
    ReportScope Scope,
    IReadOnlyList<SettingDef> Settings)
{
    /// <summary>The plugin's report object.</summary>
    public required Report Report { get; init; }

    /// <summary>The settings as stored: a competition report keeps every value once per competition.</summary>
    public IReadOnlyList<SettingDef> StoredSettings => Scope == ReportScope.Competition
        ? Settings.Select(s => s with { Scope = SettingScope.Competition }).ToList()
        : Settings;
}

/// <summary>The reports of the switched-on plugins (Р-63).</summary>
public static class PluginReports
{
    /// <summary>Every report of the switched-on plugins, plugin by plugin in catalog order.</summary>
    public static IReadOnlyList<PluginReportInfo> Enabled(IPluginCatalog? catalog)
    {
        if (catalog is null)
            return [];
        var list = new List<PluginReportInfo>();
        foreach (var plugin in catalog.Plugins.Where(p => catalog.IsEnabled(p.Info.Id)))
            foreach (var report in plugin.Reports)
                if (Describe(plugin, report) is { } info)
                    list.Add(info);
        return list;
    }

    /// <summary>The switched-on report with <paramref name="reportId"/>; null when there is none.</summary>
    public static PluginReportInfo? Find(IPluginCatalog? catalog, string? reportId) =>
        string.IsNullOrEmpty(reportId) ? null : Enabled(catalog).FirstOrDefault(r => r.Id == reportId);

    private static PluginReportInfo? Describe(LoadedPlugin plugin, Report report)
    {
        try
        {
            var pluginId = plugin.Info.Id;
            var description = report.Description;
            return new PluginReportInfo(
                pluginId,
                plugin.Info.Name,
                report.Id,
                PluginStrings.Key(pluginId, report.Name),
                string.IsNullOrWhiteSpace(description) ? null : PluginStrings.Key(pluginId, description),
                report.Scope,
                Settings(report))
            {
                Report = report
            };
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<SettingDef> Settings(Report report)
    {
        try
        {
            return report.Settings.ToList();
        }
        catch
        {
            return [];
        }
    }
}
