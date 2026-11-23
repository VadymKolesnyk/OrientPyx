using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.Localization;

namespace OrientPyx.Presentation.Services;

/// <summary>Startup glue for plugins: loads the catalog and registers every plugin's strings as a localization
/// overlay under the plugin id — the switched-off ones too, so switching one on needs no restart.</summary>
public static class PluginStartup
{
    /// <summary>Loads the plugin assemblies (the catalog does it once, lazily) — any thread, after the app database is
    /// migrated (the switched-on list lives there).</summary>
    public static void Load(IServiceProvider services) => _ = services.GetRequiredService<IPluginCatalog>().Plugins;

    /// <summary>Registers the plugins' strings — UI thread: the localization service raises its change there.</summary>
    public static void RegisterStrings(IServiceProvider services)
    {
        var catalog = services.GetRequiredService<IPluginCatalog>();
        var localization = services.GetRequiredService<ILocalizationService>();
        foreach (var plugin in catalog.Plugins)
            foreach (var (culture, strings) in plugin.Strings)
                localization.AddOverlay(culture, plugin.Info.Id, strings);
    }
}
