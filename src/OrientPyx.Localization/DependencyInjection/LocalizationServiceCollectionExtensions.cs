using Microsoft.Extensions.DependencyInjection;

namespace OrientPyx.Localization.DependencyInjection;

public static class LocalizationServiceCollectionExtensions
{
    /// <summary>Registers the localization service (Ukrainian by default).</summary>
    public static IServiceCollection AddOrientPyxLocalization(this IServiceCollection services)
    {
        services.AddSingleton<ILocalizationService, JsonLocalizationService>();
        return services;
    }
}
