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

        // Placeholder competition data services
        services.AddSingleton<ICompetitionService, CompetitionService>();
        services.AddSingleton<IParticipantService, ParticipantService>();
        services.AddSingleton<IGroupService, GroupService>();
        services.AddSingleton<ICourseService, CourseService>();
        services.AddSingleton<IPunchImportService, PunchImportService>();
        services.AddSingleton<IResultService, ResultService>();
        services.AddSingleton<IChipRentalService, ChipRentalService>();
        return services;
    }
}
