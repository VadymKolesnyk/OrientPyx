using Microsoft.Extensions.DependencyInjection;
using OrientDesk.BusinessLogic.Disciplines;
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

        // Competition-type strategies (Open/Closed): one class per discipline, resolved by enum.
        // Add a new discipline by adding a strategy class + a registration line here — nothing else.
        services.AddSingleton<IDisciplineStrategy, SetCourseStrategy>();
        services.AddSingleton<IDisciplineStrategy, ScoreByCountStrategy>();
        services.AddSingleton<IDisciplineStrategy, ScoreByTimeStrategy>();
        services.AddSingleton<IDisciplineStrategy, RogaineStrategy>();
        services.AddSingleton<IDisciplineStrategyProvider, DisciplineStrategyProvider>();

        // IOF XML import (shared by control-point and course/group imports)
        services.AddSingleton<IIofXmlParser, IofXmlParser>();

        // Course-length calculation (shared: group import today, distance display later)
        services.AddSingleton<ICourseDistanceCalculator, CourseDistanceCalculator>();

        // Placeholder competition data service (dashboard)
        services.AddSingleton<ICompetitionService, CompetitionService>();
        return services;
    }
}
