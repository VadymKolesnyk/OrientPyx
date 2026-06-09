using Microsoft.Extensions.DependencyInjection;
using OrientDesk.BusinessLogic.Interfaces;
using OrientDesk.BusinessLogic.Services;

namespace OrientDesk.BusinessLogic.DependencyInjection;

public static class BusinessLogicServiceCollectionExtensions
{
    /// <summary>Registers business-logic services.</summary>
    public static IServiceCollection AddOrientDeskBusinessLogic(this IServiceCollection services)
    {
        // Session/catalog/settings
        services.AddSingleton<IAppSettingsService, AppSettingsService>();
        services.AddSingleton<IEventCatalogService, EventCatalogService>();
        services.AddSingleton<ISessionService, SessionService>();
        services.AddSingleton<ICompetitionEditorService, CompetitionEditorService>();

        // IOF XML import (shared by control-point and, later, course/group imports)
        services.AddSingleton<IIofXmlParser, IofXmlParser>();

        // Placeholder competition data service (dashboard)
        services.AddSingleton<ICompetitionService, CompetitionService>();
        return services;
    }
}
