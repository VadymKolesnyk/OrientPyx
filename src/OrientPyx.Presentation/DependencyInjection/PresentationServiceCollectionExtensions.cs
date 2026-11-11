using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.DependencyInjection;
using OrientPyx.DataAccess.DependencyInjection;
using OrientPyx.Localization.DependencyInjection;
using OrientPyx.Presentation.Services;
using OrientPyx.Presentation.ViewModels;
using OrientPyx.Presentation.ViewModels.Pages;

namespace OrientPyx.Presentation.DependencyInjection;

public static class PresentationServiceCollectionExtensions
{
    /// <summary>Builds the application service provider, wiring every layer.</summary>
    public static IServiceProvider BuildApplicationServices()
    {
        var services = new ServiceCollection();

        // Layers
        services.AddOrientPyxLocalization();
        services.AddOrientPyxBusinessLogic();
        services.AddOrientPyxDataAccess();

        // Presentation services
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<IUiScaleService, UiScaleService>();
        services.AddSingleton<IBusyService, BusyService>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IXmlImportFlow, XmlImportFlow>();
        services.AddSingleton<IParticipantImportFlow, ParticipantImportFlow>();
        services.AddSingleton<ICsvImportFlow, CsvImportFlow>();
        services.AddSingleton<IParticipantExportFlow, ParticipantExportFlow>();
        services.AddSingleton<IExportFileSaver, ExportFileSaver>();
        services.AddSingleton<IStatementFlow, StatementFlow>();
        services.AddSingleton<IWinnersPrintFlow, WinnersPrintFlow>();
        services.AddSingleton<IEventArchiveFlow, EventArchiveFlow>();
        services.AddSingleton<ResultsDebugSnapshot>();
        services.AddSingleton<IFileReadoutPoller, FileReadoutPoller>();
        services.AddSingleton<IBackgroundActivityService, BackgroundActivityService>();
        services.AddSingleton<IToastService, ToastService>();
        services.AddSingleton<ITableLayoutStore, TableLayoutStore>();
        services.AddSingleton<IDayLockService, DayLockService>();
        services.AddSingleton<IUiPreferencesService, UiPreferencesService>();
        services.AddSingleton<IUpdateService, UpdateService>();

        // Root + gating view models
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<ViewModels.Shared.PluginNoticeViewModel>();
        // Network mode («Спільна робота»): the overlay page and the «немає зв'язку з хостом» banner.
        services.AddSingleton<NetworkViewModel>();
        services.AddSingleton<ViewModels.Shared.NetworkNoticeViewModel>();
        services.AddSingleton<EventSelectionViewModel>();
        services.AddSingleton<CreateEventViewModel>();

        // Page view models. Dashboard is a singleton like the other pages: the sidebar
        // (NavigationService) and the host (MainWindowViewModel, which wires its quick actions and
        // refreshes it on session change) must share one instance.
        services.AddSingleton<DashboardViewModel>();
        services.AddTransient<SettingsViewModel>();
        // «Про програму» — static info screen shown in a global overlay like Settings.
        services.AddSingleton<AboutViewModel>();

        // Competition info/days pages (opened from the "Competition" top menu)
        services.AddSingleton<CompetitionInfoViewModel>();
        services.AddSingleton<CompetitionDaysViewModel>();
        services.AddSingleton<ControlPointsViewModel>();
        services.AddSingleton<GroupsViewModel>();
        services.AddSingleton<ChipsViewModel>();
        services.AddSingleton<FinishReadViewModel>();
        services.AddSingleton<ParticipantsViewModel>();
        services.AddSingleton<RegionsViewModel>();
        services.AddSingleton<RelayTeamsViewModel>();
        services.AddSingleton<ClubsViewModel>();
        services.AddSingleton<DusshViewModel>();
        services.AddSingleton<RanksViewModel>();
        services.AddSingleton<PointsViewModel>();
        services.AddSingleton<EntryFeesViewModel>();
        services.AddSingleton<ProtocolsViewModel>();
        services.AddSingleton<SummaryProtocolsViewModel>();
        services.AddSingleton<StartProtocolsViewModel>();
        services.AddSingleton<OnlineResultsViewModel>();
        services.AddSingleton<MonitorResultsViewModel>();
        services.AddSingleton<SplitsExportViewModel>();
        services.AddSingleton<MapCountsViewModel>();
        services.AddSingleton<NumberVariantsViewModel>();
        services.AddSingleton<DrawViewModel>();
        services.AddSingleton<ClassicDrawViewModel>();
        services.AddSingleton<MassStartDrawViewModel>();
        services.AddSingleton<PluginsViewModel>();
        services.AddSingleton<PluginSettingsViewModel>();

        return services.BuildServiceProvider();
    }
}
