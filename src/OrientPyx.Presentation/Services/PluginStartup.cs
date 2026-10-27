using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.Localization;

namespace OrientPyx.Presentation.Services;

/// <summary>Startup glue for plugins: loads the catalog and registers every plugin's strings as a localization
/// overlay under the plugin id — the switched-off ones too, so switching one on needs no restart.</summary>
public static class PluginStartup
{
    public static void RegisterStrings(IServiceProvider services)
    {
        var catalog = services.GetRequiredService<IPluginCatalog>();
        var localization = services.GetRequiredService<ILocalizationService>();
        foreach (var plugin in catalog.Plugins)
            foreach (var (culture, strings) in plugin.Strings)
                localization.AddOverlay(culture, plugin.Info.Id, strings);
    }
}
