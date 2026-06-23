using Microsoft.Extensions.DependencyInjection;
using OrientDesk.BusinessLogic.DependencyInjection;
using OrientDesk.DataAccess.DependencyInjection;
using OrientDesk.Localization.DependencyInjection;
using OrientDesk.Presentation.Services;
using OrientDesk.Presentation.ViewModels;
using OrientDesk.Presentation.ViewModels.Pages;

namespace OrientDesk.Presentation.DependencyInjection;

public static class PresentationServiceCollectionExtensions
{
    /// <summary>Builds the application service provider, wiring every layer.</summary>
    public static IServiceProvider BuildApplicationServices()
    {
        var services = new ServiceCollection();

        // Layers
        services.AddOrientDeskLocalization();
        services.AddOrientDeskBusinessLogic();
        services.AddOrientDeskDataAccess();

        // Presentation services
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<IUiScaleService, UiScaleService>();
        services.AddSingleton<IBusyService, BusyService>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IXmlImportFlow, XmlImportFlow>();
        services.AddSingleton<IParticipantImportFlow, ParticipantImportFlow>();
        services.AddSingleton<ICsvImportFlow, CsvImportFlow>();
        services.AddSingleton<IParticipantExportFlow, ParticipantExportFlow>();
        services.AddSingleton<IFileReadoutPoller, FileReadoutPoller>();
        services.AddSingleton<IBackgroundActivityService, BackgroundActivityService>();
        services.AddSingleton<ITableLayoutStore, TableLayoutStore>();
        services.AddSingleton<IUiPreferencesService, UiPreferencesService>();

        // Root + gating view models
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<EventSelectionViewModel>();
        services.AddSingleton<CreateEventViewModel>();

        // Page view models
        services.AddTransient<DashboardViewModel>();
        services.AddTransient<SettingsViewModel>();

        // Competition info/days pages (opened from the "Competition" top menu)
        services.AddSingleton<CompetitionInfoViewModel>();
        services.AddSingleton<CompetitionDaysViewModel>();
        services.AddSingleton<ControlPointsViewModel>();
        services.AddSingleton<GroupsViewModel>();
        services.AddSingleton<ChipsViewModel>();
        services.AddSingleton<FinishReadViewModel>();
        services.AddSingleton<ParticipantsViewModel>();
        services.AddSingleton<RegionsViewModel>();
        services.AddSingleton<ClubsViewModel>();
        services.AddSingleton<DusshViewModel>();
        services.AddSingleton<RanksViewModel>();
        services.AddSingleton<PointsViewModel>();
        services.AddSingleton<EntryFeesViewModel>();
        services.AddSingleton<ProtocolsViewModel>();
        services.AddSingleton<SummaryProtocolsViewModel>();
        services.AddSingleton<StartProtocolsViewModel>();
        services.AddSingleton<SplitsExportViewModel>();
        services.AddSingleton<DrawViewModel>();
        services.AddSingleton<ClassicDrawViewModel>();

        return services.BuildServiceProvider();
    }
}
