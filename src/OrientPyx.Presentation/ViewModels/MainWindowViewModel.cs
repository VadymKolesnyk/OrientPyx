using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.BusinessLogic.Disciplines.Plugins;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models.Network;
using OrientPyx.Localization;
using OrientPyx.Presentation.Services;
using OrientPyx.Presentation.ViewModels.Pages;

namespace OrientPyx.Presentation.ViewModels;

/// <summary>
/// Root coordinator. Owns the always-visible top menu, gates the app between the competition
/// selection / creation screens and the working shell, and hosts the global Settings overlay.
/// Restores the last session on startup.
/// </summary>
public sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly ISessionService _session;
    private readonly INavigationService _navigation;
    private readonly EventSelectionViewModel _selection;
    private readonly CreateEventViewModel _create;
    private readonly ShellViewModel _shell;

    [ObservableProperty]
    private ViewModelBase _currentView;

    [ObservableProperty]
    private bool _isSettingsOpen;

    [ObservableProperty]
    private bool _isAboutOpen;

    [ObservableProperty]
    private bool _isNetworkOpen;

    // ── Network mode («Спільна робота»)
    private readonly NetworkViewModel _network;
    private readonly IWorkspaceMode _mode;
    private readonly INetworkHost _host;
    private readonly INetworkClient _client;
    private readonly IActivityLog _log;
    // The role after the last handled change; a Local↔Station switch replaces the competition list.
    private WorkspaceRole _lastRole = WorkspaceRole.Local;
    // The startup restore may connect the station itself — no list swap until it is done.
    private bool _initializing = true;
    private bool _networkShutdownDone;

    private readonly IUiScaleService _uiScale;
    private readonly IAppSettingsService _appSettings;
    private readonly IBusyService _busy;
    private readonly IDialogService _dialogs;
    private readonly IBackgroundActivityService _activities;
    private readonly Services.IEventArchiveFlow _archiveFlow;

    private readonly DashboardViewModel _dashboard;
    private readonly CompetitionInfoViewModel _competitionInfo;
    private readonly CompetitionDaysViewModel _competitionDays;
    private readonly ControlPointsViewModel _controlPoints;
    private readonly GroupsViewModel _groups;
    private readonly ChipsViewModel _chips;
    private readonly FinishReadViewModel _finishRead;
    private readonly ParticipantsViewModel _participants;
    private readonly RegionsViewModel _regions;
    private readonly RelayTeamsViewModel _relayTeams;
    private readonly ClubsViewModel _clubs;
    private readonly DusshViewModel _dussh;
    private readonly RanksViewModel _ranks;
    private readonly PointsViewModel _points;
    private readonly EntryFeesViewModel _entryFees;
    private readonly ProtocolsViewModel _protocols;
    private readonly SummaryProtocolsViewModel _summaryProtocols;
    private readonly StartProtocolsViewModel _startProtocols;
    private readonly OnlineResultsViewModel _onlineResults;
    private readonly MonitorResultsViewModel _monitorResults;
    private readonly SplitsExportViewModel _splitsExport;
    private readonly DrawViewModel _draw;
    private readonly ClassicDrawViewModel _classicDraw;
    private readonly MassStartDrawViewModel _massStartDraw;
    private readonly MapCountsViewModel _mapCounts;
    private readonly NumberVariantsViewModel _numberVariants;
    private readonly PluginsViewModel _plugins;
    private readonly PluginSettingsViewModel _pluginSettings;
    private readonly PluginReportViewModel _pluginReport;
    private readonly IPluginCatalog _pluginCatalog;

    public MainWindowViewModel(
        ISessionService session,
        INavigationService navigation,
        EventSelectionViewModel selection,
        CreateEventViewModel create,
        ShellViewModel shell,
        SettingsViewModel settings,
        AboutViewModel about,
        DashboardViewModel dashboard,
        CompetitionInfoViewModel competitionInfo,
        CompetitionDaysViewModel competitionDays,
        ControlPointsViewModel controlPoints,
        GroupsViewModel groups,
        ChipsViewModel chips,
        FinishReadViewModel finishRead,
        ParticipantsViewModel participants,
        RegionsViewModel regions,
        RelayTeamsViewModel relayTeams,
        ClubsViewModel clubs,
        DusshViewModel dussh,
        RanksViewModel ranks,
        PointsViewModel points,
        EntryFeesViewModel entryFees,
        ProtocolsViewModel protocols,
        SummaryProtocolsViewModel summaryProtocols,
        StartProtocolsViewModel startProtocols,
        OnlineResultsViewModel onlineResults,
        MonitorResultsViewModel monitorResults,
        SplitsExportViewModel splitsExport,
        DrawViewModel draw,
        ClassicDrawViewModel classicDraw,
        MassStartDrawViewModel massStartDraw,
        MapCountsViewModel mapCounts,
        NumberVariantsViewModel numberVariants,
        PluginsViewModel plugins,
        PluginSettingsViewModel pluginSettings,
        PluginReportViewModel pluginReport,
        NetworkViewModel network,
        IWorkspaceMode mode,
        INetworkHost host,
        INetworkClient client,
        IActivityLog log,
        IPluginCatalog pluginCatalog,
        ILocalizationService localization,
        IUiScaleService uiScale,
        IAppSettingsService appSettings,
        IBusyService busy,
        IDialogService dialogs,
        IBackgroundActivityService activities,
        IToastService toasts,
        Services.IEventArchiveFlow archiveFlow,
        Services.ResultsDebugSnapshot resultsSnapshot)
    {
        _resultsSnapshot = resultsSnapshot;
        _session = session;
        _navigation = navigation;
        _selection = selection;
        _create = create;
        _shell = shell;
        Settings = settings;
        About = about;
        _dashboard = dashboard;
        _competitionInfo = competitionInfo;
        _competitionDays = competitionDays;
        _controlPoints = controlPoints;
        _groups = groups;
        _chips = chips;
        _finishRead = finishRead;
        _participants = participants;
        _regions = regions;
        _relayTeams = relayTeams;
        _clubs = clubs;
        _dussh = dussh;
        _ranks = ranks;
        _points = points;
        _entryFees = entryFees;
        _protocols = protocols;
        _summaryProtocols = summaryProtocols;
        _startProtocols = startProtocols;
        _onlineResults = onlineResults;
        _monitorResults = monitorResults;
        _splitsExport = splitsExport;
        _draw = draw;
        _classicDraw = classicDraw;
        _massStartDraw = massStartDraw;
        _mapCounts = mapCounts;
        _numberVariants = numberVariants;
        _plugins = plugins;
        _pluginSettings = pluginSettings;
        _pluginReport = pluginReport;
        _pluginCatalog = pluginCatalog;
        _network = network;
        _mode = mode;
        _host = host;
        _client = client;
        _log = log;
        Localization = localization;
        _uiScale = uiScale;
        _appSettings = appSettings;
        _busy = busy;
        _dialogs = dialogs;
        _activities = activities;
        Toasts = toasts;
        _archiveFlow = archiveFlow;

        Pages = navigation.Pages;

        _selection.CreateRequested += (_, _) => ShowCreate();
        // Runs inside the create operation's busy scope, so the loader stays up until the list is ready.
        // A new competition opens straight away on its first day; the refreshed picker stays only if it cannot.
        _create.OnCreatedAsync = async created =>
        {
            await ShowSelectionAsync();
            if (await _selection.TryOpenAsync(created.Identifier, dayNumber: null))
                _log.Action($"Opened the new competition '{created.Identifier}'");
        };
        _create.Cancelled += async (_, _) => await ShowSelectionAsync();
        _shell.ChangeEventRequested += async (_, _) => await ChangeEventInternalAsync();
        // The dashboard's live tiles auto-refresh only while it is the shown page — start that when it
        // becomes the selected page and stop it when anything else takes over. Watched here (not on the
        // sidebar Navigate command) so it covers every open path, including the top-menu ones that set
        // SelectedPage directly. A cycle-free wiring: the dashboard can't depend on the shell/navigation
        // (NavigationService is built from the dashboard), so the host — which holds both — bridges them.
        _shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(ShellViewModel.SelectedPage))
                return;
            if (ReferenceEquals(_shell.SelectedPage, _dashboard))
                _dashboard.StartAutoRefresh();
            else
                _dashboard.StopAutoRefresh();
        };
        // "Go to settings" on the chip auto-read activity opens the Chips page.
        _chips.NavigateToSelfRequested += async (_, _) => await OpenChipsAsync();
        // Same for the finish-read auto-read activity.
        _finishRead.NavigateToSelfRequested += async (_, _) => await OpenFinishReadAsync();
        // "Go to settings" on the online-results publish activity opens the Online-results page.
        _onlineResults.NavigateToSelfRequested += async (_, _) => await OpenOnlineResultsAsync();
        // Same for the results-monitor generation activity.
        _monitorResults.NavigateToSelfRequested += async (_, _) => await OpenMonitorResultsAsync();
        // Dashboard quick-action buttons route through here (the dashboard owns no page-open commands).
        _dashboard.QuickActionRequested += async (_, action) => await OnDashboardQuickActionAsync(action);
        // A plugin row's «Налаштування» opens that plugin's page; switching a plugin on/off rebuilds its menu items.
        _plugins.OpenSettingsRequested += async (_, pluginId) => await OpenPluginSettingsAsync(pluginId);
        _pluginCatalog.EnabledChanged += (_, _) => Dispatcher.UIThread.Post(RefreshPluginMenu);
        RefreshPluginMenu();
        _session.SessionChanged += OnSessionChanged;
        Localization.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(WindowTitle));
            OnPropertyChanged(nameof(RoleIndicatorText));
            // The plugins' report names are translated.
            RefreshPluginMenu();
        };

        // Network mode: the picker's «Підключитися до хоста…» and the top-bar activity's «Відкрити налаштування»
        // open the overlay; a role switch (Local ↔ Station) swaps the competition list for the host's (or back).
        // The transport raises these on its own threads.
        _selection.ConnectRequested += (_, _) => OpenNetwork();
        _network.OpenRequested += (_, _) => Dispatcher.UIThread.Post(OpenNetwork);
        _mode.Changed += (_, _) => Dispatcher.UIThread.Post(OnRoleChanged);
        _host.Changed += (_, _) => Dispatcher.UIThread.Post(RefreshRoleIndicator);
        _client.Changed += (_, _) => Dispatcher.UIThread.Post(RefreshRoleIndicator);

        // Every page's «?» header button raises HelpRequested; the root coordinator owns the dialog
        // service, so it opens the per-screen help modal here (same wiring as the quick-action events).
        foreach (var page in new PageViewModelBase[]
                 {
                     _dashboard, _competitionInfo, _competitionDays, _controlPoints, _groups, _chips,
                     _finishRead, _participants, _relayTeams, _regions, _clubs, _dussh, _ranks, _points, _entryFees,
                     _protocols, _summaryProtocols, _startProtocols, _onlineResults, _monitorResults,
                     _splitsExport, _draw, _classicDraw, _massStartDraw, _mapCounts, _numberVariants, _plugins, _pluginSettings, Settings, _network,
                 })
        {
            page.HelpRequested += OnPageHelpRequested;
            page.BlockHelpRequested += OnPageBlockHelpRequested;
        }

        // Initial view; replaced by InitializeAsync.
        _currentView = _selection;
    }

    public ILocalizationService Localization { get; }

    /// <summary>Exposed for the root FontSize binding (global font scaling).</summary>
    public IUiScaleService UiScale => _uiScale;

    /// <summary>Exposed for the global loader overlay.</summary>
    public IBusyService Busy => _busy;

    /// <summary>Exposed for the global modal-dialog overlay.</summary>
    public IDialogService Dialogs => _dialogs;

    /// <summary>Exposed for the top-bar running-processes block.</summary>
    public IBackgroundActivityService Activities => _activities;

    /// <summary>Exposed for the bottom-right toast stack (non-blocking notices over every page).</summary>
    public IToastService Toasts { get; }

    public SettingsViewModel Settings { get; }

    /// <summary>«Про програму» content shown in the global overlay.</summary>
    public AboutViewModel About { get; }

    /// <summary>«Спільна робота» (network mode) content shown in its global overlay.</summary>
    public NetworkViewModel Network => _network;

    /// <summary>True while the working shell is active (enables the "change competition" item).</summary>
    public bool IsEventSelected => _session.HasSelection && !IsSettingsOpen && !IsNetworkOpen;

    /// <summary>True while this instance works on a host's data over the network.</summary>
    public bool IsStation => _mode.IsStation;

    /// <summary>The Debug results snapshot writes into the competition folder — there is none on a station.</summary>
    public bool ShowDebugSnapshot => IsDebugBuild && !IsStation;

    /// <summary>«Хост · станцій: 2» / «Станція · Головний-ноут» — the top-bar role button, hidden while local.</summary>
    public string RoleIndicatorText => _mode.Role switch
    {
        WorkspaceRole.Host => string.Format(Localization.Get("Network.Role.Host"),
            _host.IsRunning ? _host.Stations.Count(s => s.IsOnline) : 0),
        WorkspaceRole.Station => string.Format(Localization.Get("Network.Role.Station"),
            _mode.Station?.HostName ?? _client.Host?.HostName ?? string.Empty),
        _ => string.Empty,
    };

    public bool IsRoleIndicatorVisible => _mode.Role != WorkspaceRole.Local;

    /// <summary>Navigation pages, surfaced as top-menu items once a competition is open.</summary>
    public ReadOnlyObservableCollection<PageViewModelBase> Pages { get; }

    /// <summary>
    /// Window title: the app name, plus the active competition once one is selected,
    /// e.g. "OrientPyx — City Championship 2026". The day is now a per-page concern.
    /// </summary>
    public string WindowTitle
    {
        get
        {
            var appName = Localization.Get("App.Title");
            if (!_session.HasSelection)
                return appName;

            var name = _session.CurrentEvent?.Name ?? string.Empty;
            var title = $"{appName} — {name}";
            return _mode.IsStation
                ? string.Format(Localization.Get("App.Title.Station"), title, _mode.Station?.HostName ?? string.Empty)
                : title;
        }
    }

    [RelayCommand]
    private void Navigate(PageViewModelBase? page)
    {
        if (page is null)
            return;

        // Setting SelectedPage drives the dashboard's auto-refresh start/stop (and its initial reload)
        // via the _shell.PropertyChanged handler wired in the constructor — no explicit reload here.
        _shell.SelectedPage = page;
    }

    /// <summary>Opens the dashboard (Панель) — the only navigable sidebar page.</summary>
    [RelayCommand]
    private void OpenDashboard() => Navigate(_dashboard);

    [RelayCommand(CanExecute = nameof(CanChangeEvent))]
    private async Task OpenCompetitionInfoAsync()
    {
        await _competitionInfo.LoadAsync();
        _shell.SelectedPage = _competitionInfo;
    }

    [RelayCommand(CanExecute = nameof(CanChangeEvent))]
    private async Task OpenCompetitionDaysAsync()
    {
        await _competitionDays.LoadAsync();
        _shell.SelectedPage = _competitionDays;
    }

    [RelayCommand(CanExecute = nameof(CanChangeEvent))]
    private async Task OpenControlPointsAsync()
    {
        await _controlPoints.LoadAsync();
        _shell.SelectedPage = _controlPoints;
    }

    [RelayCommand(CanExecute = nameof(CanChangeEvent))]
    private async Task OpenGroupsAsync()
    {
        await _groups.LoadAsync();
        _shell.SelectedPage = _groups;
    }

    [RelayCommand(CanExecute = nameof(CanChangeEvent))]
    private async Task OpenChipsAsync()
    {
        await _chips.LoadAsync();
        _shell.SelectedPage = _chips;
    }

    [RelayCommand(CanExecute = nameof(CanChangeEvent))]
    private async Task OpenParticipantsAsync()
    {
        await _participants.LoadAsync();
        _shell.SelectedPage = _participants;
    }

    [RelayCommand(CanExecute = nameof(CanChangeEvent))]
    private async Task OpenFinishReadAsync()
    {
        await _finishRead.LoadAsync();
        _shell.SelectedPage = _finishRead;
    }

    [RelayCommand(CanExecute = nameof(CanChangeEvent))]
    private async Task OpenRelayTeamsAsync()
    {
        await _relayTeams.LoadAsync();
        _shell.SelectedPage = _relayTeams;
    }

    [RelayCommand(CanExecute = nameof(CanChangeEvent))]
    private async Task OpenRegionsAsync()
    {
        await _regions.LoadAsync();
        _shell.SelectedPage = _regions;
    }

    [RelayCommand(CanExecute = nameof(CanChangeEvent))]
    private async Task OpenClubsAsync()
    {
        await _clubs.LoadAsync();
        _shell.SelectedPage = _clubs;
    }

    [RelayCommand(CanExecute = nameof(CanChangeEvent))]
    private async Task OpenDusshAsync()
    {
        await _dussh.LoadAsync();
        _shell.SelectedPage = _dussh;
    }

    [RelayCommand(CanExecute = nameof(CanChangeEvent))]
    private async Task OpenRanksAsync()
    {
        await _ranks.LoadAsync();
        _shell.SelectedPage = _ranks;
    }

    [RelayCommand(CanExecute = nameof(CanChangeEvent))]
    private async Task OpenPointsAsync()
    {
        await _points.LoadAsync();
        _shell.SelectedPage = _points;
    }

    [RelayCommand(CanExecute = nameof(CanChangeEvent))]
    private async Task OpenEntryFeesAsync()
    {
        await _entryFees.LoadAsync();
        _shell.SelectedPage = _entryFees;
    }

    [RelayCommand(CanExecute = nameof(CanChangeEvent))]
    private async Task OpenProtocolsAsync()
    {
        await _protocols.LoadAsync();
        _shell.SelectedPage = _protocols;
    }

    [RelayCommand(CanExecute = nameof(CanChangeEvent))]
    private async Task OpenSummaryProtocolAsync()
    {
        await _summaryProtocols.LoadAsync();
        _shell.SelectedPage = _summaryProtocols;
    }

    [RelayCommand(CanExecute = nameof(CanChangeEvent))]
    private Task OpenStartProtocolAsync() => OpenStartProtocolKindAsync(BusinessLogic.Models.StartProtocolKind.Regular);

    [RelayCommand(CanExecute = nameof(CanChangeEvent))]
    private Task OpenStartProtocolJudgesAsync() => OpenStartProtocolKindAsync(BusinessLogic.Models.StartProtocolKind.Judges);

    // The start-protocol page is one singleton VM reused for both kinds: set the kind (and re-raise its
    // nav/title labels) before loading so the heading + saved template match the chosen protocol.
    private async Task OpenStartProtocolKindAsync(BusinessLogic.Models.StartProtocolKind kind)
    {
        _startProtocols.Kind = kind;
        _startProtocols.RaiseKindLabels();
        await _startProtocols.LoadAsync();
        _shell.SelectedPage = _startProtocols;
    }

    [RelayCommand(CanExecute = nameof(CanChangeEvent))]
    private async Task OpenOnlineResultsAsync()
    {
        await _onlineResults.LoadAsync();
        _shell.SelectedPage = _onlineResults;
    }

    [RelayCommand(CanExecute = nameof(CanChangeEvent))]
    private async Task OpenMonitorResultsAsync()
    {
        await _monitorResults.LoadAsync();
        _shell.SelectedPage = _monitorResults;
    }

    [RelayCommand(CanExecute = nameof(CanChangeEvent))]
    private async Task OpenSplitsExportAsync()
    {
        await _splitsExport.LoadAsync();
        _shell.SelectedPage = _splitsExport;
    }

    [RelayCommand(CanExecute = nameof(CanChangeEvent))]
    private async Task OpenDrawAsync()
    {
        await _draw.LoadAsync();
        _shell.SelectedPage = _draw;
    }

    [RelayCommand(CanExecute = nameof(CanChangeEvent))]
    private async Task OpenMapCountsAsync()
    {
        await _mapCounts.LoadAsync();
        _shell.SelectedPage = _mapCounts;
    }

    [RelayCommand(CanExecute = nameof(CanChangeEvent))]
    private async Task OpenNumberVariantsAsync()
    {
        await _numberVariants.LoadAsync();
        _shell.SelectedPage = _numberVariants;
    }

    [RelayCommand(CanExecute = nameof(CanChangeEvent))]
    private async Task OpenClassicDrawAsync()
    {
        await _classicDraw.LoadAsync();
        _shell.SelectedPage = _classicDraw;
    }

    [RelayCommand(CanExecute = nameof(CanChangeEvent))]
    private async Task OpenMassStartDrawAsync()
    {
        await _massStartDraw.LoadAsync();
        _shell.SelectedPage = _massStartDraw;
    }

    // ── «Плагіни» menu: the management page, then one item per switched-on plugin — its settings page, or a submenu
    // with the settings and the plugin's reports (Р-63).

    /// <summary>The switched-on plugins, in catalog order — the dynamic part of the «Плагіни» menu (built by the view).</summary>
    public ObservableCollection<PluginMenuEntry> PluginMenu { get; } = [];

    private void RefreshPluginMenu()
    {
        var reports = PluginReports.Enabled(_pluginCatalog);
        PluginMenu.Clear();
        foreach (var plugin in _pluginCatalog.Plugins.Where(p => _pluginCatalog.IsEnabled(p.Info.Id)))
            PluginMenu.Add(new PluginMenuEntry(plugin.Info.Id, plugin.Info.Name)
            {
                Reports = reports
                    .Where(r => r.PluginId == plugin.Info.Id)
                    .Select(r => new PluginReportMenuEntry(r.Id, Localization.Get(r.NameKey)))
                    .ToList()
            });
    }

    /// <summary>A plugin report's page: one singleton VM for every report — set the report before loading.</summary>
    [RelayCommand(CanExecute = nameof(CanChangeEvent))]
    private async Task OpenPluginReportAsync(string? reportId)
    {
        if (string.IsNullOrEmpty(reportId))
            return;
        _pluginReport.ReportId = reportId;
        await _pluginReport.LoadAsync();
        _shell.SelectedPage = _pluginReport;
    }

    [RelayCommand(CanExecute = nameof(CanChangeEvent))]
    private async Task OpenPluginsAsync()
    {
        await _plugins.LoadAsync();
        _shell.SelectedPage = _plugins;
    }

    // The plugin-settings page is one singleton VM reused for every plugin: set the plugin before loading.
    [RelayCommand(CanExecute = nameof(CanChangeEvent))]
    private async Task OpenPluginSettingsAsync(string? pluginId)
    {
        if (string.IsNullOrEmpty(pluginId))
            return;
        _pluginSettings.PluginId = pluginId;
        await _pluginSettings.LoadAsync();
        _shell.SelectedPage = _pluginSettings;
    }

    // Routes a dashboard quick-action to the matching page-open command (guarded by HasSelection like
    // the menu commands). Kept here because the page-open commands live on this root coordinator.
    private async Task OnDashboardQuickActionAsync(DashboardQuickAction action)
    {
        if (!_session.HasSelection)
            return;

        switch (action)
        {
            case DashboardQuickAction.Participants:
                await OpenParticipantsAsync();
                break;
            case DashboardQuickAction.FinishRead:
                await OpenFinishReadAsync();
                break;
            case DashboardQuickAction.Draw:
                await OpenDrawAsync();
                break;
            case DashboardQuickAction.StartProtocol:
                await OpenStartProtocolAsync();
                break;
            case DashboardQuickAction.Protocols:
                await OpenProtocolsAsync();
                break;
            case DashboardQuickAction.Splits:
                await OpenSplitsExportAsync();
                break;
            case DashboardQuickAction.Groups:
                await OpenGroupsAsync();
                break;
            case DashboardQuickAction.Chips:
                await OpenChipsAsync();
                break;
            case DashboardQuickAction.ParticipantsWithoutChip:
                _participants.RequestQuickFilter(ParticipantQuickFilter.WithoutChip);
                await OpenParticipantsAsync();
                break;
            case DashboardQuickAction.ParticipantsWithoutGroup:
                _participants.RequestQuickFilter(ParticipantQuickFilter.WithoutGroup);
                await OpenParticipantsAsync();
                break;
            case DashboardQuickAction.ParticipantsOnCourse:
                _participants.RequestQuickFilter(ParticipantQuickFilter.OnCourse);
                await OpenParticipantsAsync();
                break;
            case DashboardQuickAction.MonitorResults:
                await OpenMonitorResultsAsync();
                break;
            case DashboardQuickAction.OnlineResults:
                await OpenOnlineResultsAsync();
                break;
        }
    }

    // Opens the per-screen help modal for whichever page raised the event.
    private void OnPageHelpRequested(object? sender, EventArgs e)
    {
        if (sender is not PageViewModelBase page)
            return;

        _ = _dialogs.ShowScreenHelpAsync(
            new ViewModels.Dialogs.ScreenHelpViewModel(Localization, page.HelpKeyPrefix));
    }

    // Opens the per-block help modal for a section that raised the event with its own key prefix.
    private void OnPageBlockHelpRequested(object? sender, string keyPrefix)
        => _ = _dialogs.ShowScreenHelpAsync(
            new ViewModels.Dialogs.ScreenHelpViewModel(Localization, keyPrefix));

    /// <summary>Called once after construction to restore the last session or show the picker.</summary>
    public async Task InitializeAsync()
    {
        // BD work runs off the UI thread inside RunAsync; UI-state writes happen after the await.
        string savedLanguage = string.Empty;
        bool restored;
        try
        {
            restored = await _busy.RunAsync(async () =>
            {
                await _uiScale.InitializeAsync();
                savedLanguage = await _appSettings.GetLanguageAsync();
                return await _session.TryRestoreLastAsync();
            });
        }
        catch (Exception)
        {
            // Restoring the last competition failed (its database is unreadable or gone). The busy service
            // has already logged the exception; fall through to the picker instead of surfacing a crash —
            // nothing awaits this task, so a throw here would reach the unobserved-exception handler.
            restored = false;
        }

        // Restore the saved UI language (SetLanguage touches culture/UI, so apply after the await).
        ApplySavedLanguage(savedLanguage);

        if (restored)
            ShowShell();
        else
            await ShowSelectionAsync();

        // From here on a role switch swaps the competition list (the restore above may have connected the station).
        _lastRole = _mode.Role;
        _initializing = false;
        RefreshRoleIndicator();

        // The host autostarts when it was switched on last time; never fatal (the overlay shows the error).
        await _network.AutoStartHostAsync();
    }

    // Applies a persisted language culture name at startup, if one was saved and is available.
    // SetLanguage itself no-ops when the culture is already active or not loaded.
    private void ApplySavedLanguage(string cultureName)
    {
        if (string.IsNullOrWhiteSpace(cultureName))
            return;

        var culture = Localization.AvailableCultures
            .FirstOrDefault(c => string.Equals(c.Name, cultureName, StringComparison.OrdinalIgnoreCase));
        if (culture is not null)
            Localization.SetLanguage(culture);
    }

    [RelayCommand]
    private void OpenSettings() => IsSettingsOpen = true;

    [RelayCommand]
    private void CloseSettings() => IsSettingsOpen = false;

    [RelayCommand]
    private void OpenAbout() => IsAboutOpen = true;

    [RelayCommand]
    private void CloseAbout() => IsAboutOpen = false;

    [RelayCommand]
    private void OpenNetwork()
    {
        if (IsNetworkOpen)
            return;
        IsNetworkOpen = true;
        _ = _network.OnOpenedAsync();
    }

    [RelayCommand]
    private void CloseNetwork()
    {
        if (!IsNetworkOpen)
            return;
        IsNetworkOpen = false;
        _network.OnClosed();
    }

    partial void OnIsNetworkOpenChanged(bool value) => OnPropertyChanged(nameof(IsEventSelected));

    // The role changed (host switched on/off, station connected/disconnected). Only a switch into or out of the
    // station role changes WHOSE competitions are listed: drop the session and re-show the picker, which now reads
    // the host's list (or the local one again). Hosting keeps the local data, so the open competition stays.
    private void OnRoleChanged()
    {
        var role = _mode.Role;
        RefreshRoleIndicator();
        if (_initializing || role == _lastRole)
        {
            _lastRole = role;
            return;
        }
        var wasStation = _lastRole == WorkspaceRole.Station;
        _lastRole = role;
        if (!wasStation && role != WorkspaceRole.Station)
            return;

        _log.Action(role == WorkspaceRole.Station
            ? $"Network: now a station of '{_mode.Station?.HostName}'"
            : "Network: back to local data");
        _dashboard.StopAutoRefresh();
        _session.Clear();
        _ = ShowSelectionSafelyAsync(role == WorkspaceRole.Station ? _client.HostCurrentCompetition : null);
    }

    // After a station connects it lands in the competition and day the host has open; the picker shows only when
    // the host has none open (or it is not in the list).
    private async Task ShowSelectionSafelyAsync((string Identifier, int? DayNumber)? openLikeHost = null)
    {
        try
        {
            await ShowSelectionAsync();
            if (openLikeHost is { } target && await _selection.TryOpenAsync(target.Identifier, target.DayNumber))
                _log.Action($"Network: opened the host's competition '{target.Identifier}'");
        }
        catch (Exception ex)
        {
            // The busy service has already logged a store failure; the picker stays with its previous list.
            _log.Error("Competition list after a role change", ex);
            CurrentView = _selection;
        }
    }

    private void RefreshRoleIndicator()
    {
        OnPropertyChanged(nameof(RoleIndicatorText));
        OnPropertyChanged(nameof(IsRoleIndicatorVisible));
        OnPropertyChanged(nameof(IsStation));
        OnPropertyChanged(nameof(ShowDebugSnapshot));
        OnPropertyChanged(nameof(WindowTitle));
    }

    /// <summary>True once the network was shut down for exit (the window's Closing handler then lets the close through).</summary>
    public bool NetworkShutdownDone => _networkShutdownDone;

    /// <summary>Stops the host / leaves the host before exit, best-effort and bounded to ~2 s.</summary>
    public async Task ShutdownNetworkAsync()
    {
        if (_networkShutdownDone)
            return;
        _networkShutdownDone = true;
        try
        {
            await _network.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            _log.Error("Network shutdown on exit", ex);
        }
    }

    private bool CanChangeEvent() => _session.HasSelection;

    /// <summary>Exposed so the main window can hand the flow its file picker (needs the window's StorageProvider).</summary>
    public Services.IEventArchiveFlow ArchiveFlow => _archiveFlow;

    [RelayCommand(CanExecute = nameof(CanChangeEvent))]
    private Task ChangeEventAsync() => ChangeEventInternalAsync();

    private readonly Services.ResultsDebugSnapshot _resultsSnapshot;

    /// <summary>True in a Debug build — shows developer-only menu items (results snapshot).</summary>
#if DEBUG
    public bool IsDebugBuild => true;
#else
    public bool IsDebugBuild => false;
#endif

    // Debug: writes the computed results of every day to events/<id>/_debug/results-*.json for diffing.
    [RelayCommand(CanExecute = nameof(CanChangeEvent))]
    private async Task WriteResultsSnapshotAsync()
    {
        var path = await _resultsSnapshot.WriteAsync();
        // Developer tool: reveal the written file in the file manager rather than a localized dialog.
        Services.FileReveal.TryReveal(path);
    }

    // Exports the currently-open competition to a single archive file (File → Export competition).
    [RelayCommand(CanExecute = nameof(CanChangeEvent))]
    private async Task ExportCompetitionAsync()
    {
        var current = _session.CurrentEvent;
        if (current is null)
            return;

        await _archiveFlow.ExportAsync(current);
    }

    // Imports a competition archive as a new competition (File → Import competition). Available even
    // with no competition open; on success the imported competition opens straight away.
    [RelayCommand]
    private Task ImportCompetitionAsync() => AfterImportAsync(_archiveFlow.ImportAsync);

    /// <summary>
    /// Imports an archive file the app was launched with (a double-clicked <c>.opyx</c>) — the same flow as
    /// File → Import competition, starting from the given file instead of the open-file dialog.
    /// </summary>
    public Task ImportCompetitionFileAsync(string path) => AfterImportAsync(() => _archiveFlow.ImportFileAsync(path));

    private async Task AfterImportAsync(Func<Task<BusinessLogic.Models.EventSummary?>> import)
    {
        IsSettingsOpen = false;
        var imported = await import();
        if (imported is null)
            return;

        // Refresh the picker so the new competition is in the list, then open it on its first day. If it cannot be
        // opened (no days, gone from the list), the user stays on the refreshed picker.
        _dashboard.StopAutoRefresh();
        _session.Clear();
        await ShowSelectionAsync();
        if (await _selection.TryOpenAsync(imported.Identifier, dayNumber: null))
            _log.Action($"Opened the imported competition '{imported.Identifier}'");
    }

    private async Task ChangeEventInternalAsync()
    {
        IsSettingsOpen = false;
        // Leaving the shell for the selection screen doesn't change SelectedPage, so stop the
        // dashboard's auto-refresh explicitly — otherwise it would keep polling a hidden page.
        _dashboard.StopAutoRefresh();
        _session.Clear();
        await ShowSelectionAsync();
    }

    [RelayCommand]
    private async Task ExitAsync()
    {
        await ShutdownNetworkAsync();
        if (Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    private void OnSessionChanged(object? sender, EventArgs e)
    {
        // SessionChanged can be raised on a pool thread (session writes run inside RunAsync); this
        // handler touches commands and CurrentView, so hop to the UI thread before doing anything.
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnSessionChanged(sender, e));
            return;
        }

        // On a station the identifier names the competition on the host (for its version poll / log batches).
        _client.NotifyCurrentEvent(_session.CurrentEvent?.Identifier);

        ChangeEventCommand.NotifyCanExecuteChanged();
        ExportCompetitionCommand.NotifyCanExecuteChanged();
        WriteResultsSnapshotCommand.NotifyCanExecuteChanged();
        OpenCompetitionInfoCommand.NotifyCanExecuteChanged();
        OpenCompetitionDaysCommand.NotifyCanExecuteChanged();
        OpenControlPointsCommand.NotifyCanExecuteChanged();
        OpenGroupsCommand.NotifyCanExecuteChanged();
        OpenChipsCommand.NotifyCanExecuteChanged();
        OpenFinishReadCommand.NotifyCanExecuteChanged();
        OpenParticipantsCommand.NotifyCanExecuteChanged();
        OpenRegionsCommand.NotifyCanExecuteChanged();
        OpenRelayTeamsCommand.NotifyCanExecuteChanged();
        OpenClubsCommand.NotifyCanExecuteChanged();
        OpenDusshCommand.NotifyCanExecuteChanged();
        OpenRanksCommand.NotifyCanExecuteChanged();
        OpenPointsCommand.NotifyCanExecuteChanged();
        OpenEntryFeesCommand.NotifyCanExecuteChanged();
        OpenProtocolsCommand.NotifyCanExecuteChanged();
        OpenSummaryProtocolCommand.NotifyCanExecuteChanged();
        OpenStartProtocolCommand.NotifyCanExecuteChanged();
        OpenStartProtocolJudgesCommand.NotifyCanExecuteChanged();
        OpenOnlineResultsCommand.NotifyCanExecuteChanged();
        OpenMonitorResultsCommand.NotifyCanExecuteChanged();
        OpenSplitsExportCommand.NotifyCanExecuteChanged();
        OpenDrawCommand.NotifyCanExecuteChanged();
        OpenClassicDrawCommand.NotifyCanExecuteChanged();
        OpenMassStartDrawCommand.NotifyCanExecuteChanged();
        OpenPluginsCommand.NotifyCanExecuteChanged();
        OpenPluginSettingsCommand.NotifyCanExecuteChanged();
        OpenPluginReportCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsEventSelected));
        OnPropertyChanged(nameof(WindowTitle));

        if (_session.HasSelection)
            ShowShell();
    }

    partial void OnIsSettingsOpenChanged(bool value) => OnPropertyChanged(nameof(IsEventSelected));

    private async Task ShowSelectionAsync()
    {
        // Fetch the list off the UI thread, then populate the collection and swap views on it.
        var events = await _busy.RunAsync(() => _selection.FetchEventsAsync());
        _selection.Populate(events);
        CurrentView = _selection;
    }

    private void ShowCreate()
    {
        _create.Reset();
        CurrentView = _create;
    }

    private void ShowShell()
    {
        CurrentView = _shell;
        // On entering the shell the dashboard is usually the already-selected page, so SelectedPage
        // won't change and the PropertyChanged handler won't fire — kick off its auto-refresh here.
        if (ReferenceEquals(_shell.SelectedPage, _dashboard))
            _dashboard.StartAutoRefresh();
    }
}

/// <summary>One switched-on plugin in the «Плагіни» menu.</summary>
public sealed record PluginMenuEntry(string Id, string Name)
{
    /// <summary>The plugin's reports; with any, the plugin's item is a submenu (settings + reports).</summary>
    public IReadOnlyList<PluginReportMenuEntry> Reports { get; init; } = [];
}

/// <summary>One plugin report in its plugin's submenu: its id and translated name.</summary>
public sealed record PluginReportMenuEntry(string Id, string Name);
