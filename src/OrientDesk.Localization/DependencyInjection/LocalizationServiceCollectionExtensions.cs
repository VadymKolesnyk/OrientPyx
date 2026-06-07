using Microsoft.Extensions.DependencyInjection;

namespace OrientDesk.Localization.DependencyInjection;

public static class LocalizationServiceCollectionExtensions
{
    /// <summary>Registers the localization service (Ukrainian by default).</summary>
    public static IServiceCollection AddOrientDeskLocalization(this IServiceCollection services)
    {
        services.AddSingleton<ILocalizationService, JsonLocalizationService>();
        return services;
    }
}
