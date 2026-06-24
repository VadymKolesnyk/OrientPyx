using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientDesk.BusinessLogic.Interfaces;
using OrientDesk.Localization;
using OrientDesk.Presentation.Services;
using OrientDesk.Presentation.ViewModels.Pages;

namespace OrientDesk.Presentation.ViewModels;

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

    private readonly IUiScaleService _uiScale;
    private readonly IBusyService _busy;
    private readonly IDialogService _dialogs;
    private readonly IBackgroundActivityService _activities;

    private readonly DashboardViewModel _dashboard;
    private readonly CompetitionInfoViewModel _competitionInfo;
    private readonly CompetitionDaysViewModel _competitionDays;
    private readonly ControlPointsViewModel _controlPoints;
    private readonly GroupsViewModel _groups;
    private readonly ChipsViewModel _chips;
    private readonly FinishReadViewModel _finishRead;
    private readonly ParticipantsViewModel _participants;
    private readonly RegionsViewModel _regions;
    private readonly ClubsViewModel _clubs;
    private readonly DusshViewModel _dussh;
    private readonly RanksViewModel _ranks;
    private readonly PointsViewModel _points;
    private readonly EntryFeesViewModel _entryFees;
    private readonly ProtocolsViewModel _protocols;
    private readonly SummaryProtocolsViewModel _summaryProtocols;
    private readonly StartProtocolsViewModel _startProtocols;
    private readonly OnlineResultsViewModel _onlineResults;
    private readonly SplitsExportViewModel _splitsExport;
    private readonly DrawViewModel _draw;
    private readonly ClassicDrawViewModel _classicDraw;

    public MainWindowViewModel(
        ISessionService session,
        INavigationService navigation,
        EventSelectionViewModel selection,
        CreateEventViewModel create,
        ShellViewModel shell,
        SettingsViewModel settings,
        DashboardViewModel dashboard,
        CompetitionInfoViewModel competitionInfo,
        CompetitionDaysViewModel competitionDays,
        ControlPointsViewModel controlPoints,
        GroupsViewModel groups,
        ChipsViewModel chips,
        FinishReadViewModel finishRead,
        ParticipantsViewModel participants,
        RegionsViewModel regions,
        ClubsViewModel clubs,
        DusshViewModel dussh,
        RanksViewModel ranks,
        PointsViewModel points,
        EntryFeesViewModel entryFees,
        ProtocolsViewModel protocols,
        SummaryProtocolsViewModel summaryProtocols,
        StartProtocolsViewModel startProtocols,
        OnlineResultsViewModel onlineResults,
        SplitsExportViewModel splitsExport,
        DrawViewModel draw,
        ClassicDrawViewModel classicDraw,
        ILocalizationService localization,
        IUiScaleService uiScale,
        IBusyService busy,
        IDialogService dialogs,
        IBackgroundActivityService activities)
    {
        _session = session;
        _navigation = navigation;
        _selection = selection;
        _create = create;
        _shell = shell;
        Settings = settings;
        _dashboard = dashboard;
        _competitionInfo = competitionInfo;
        _competitionDays = competitionDays;
        _controlPoints = controlPoints;
        _groups = groups;
        _chips = chips;
        _finishRead = finishRead;
        _participants = participants;
        _regions = regions;
        _clubs = clubs;
        _dussh = dussh;
        _ranks = ranks;
        _points = points;
        _entryFees = entryFees;
        _protocols = protocols;
        _summaryProtocols = summaryProtocols;
        _startProtocols = startProtocols;
        _onlineResults = onlineResults;
        _splitsExport = splitsExport;
        _draw = draw;
        _classicDraw = classicDraw;
        Localization = localization;
        _uiScale = uiScale;
        _busy = busy;
        _dialogs = dialogs;
        _activities = activities;

        Pages = navigation.Pages;

        _selection.CreateRequested += (_, _) => ShowCreate();
        // Runs inside the create operation's busy scope, so the loader stays up until the list is ready.
        _create.OnCreatedAsync = async _ => await ShowSelectionAsync();
        _create.Cancelled += async (_, _) => await ShowSelectionAsync();
        _shell.ChangeEventRequested += async (_, _) => await ChangeEventInternalAsync();
        // "Go to settings" on the chip auto-read activity opens the Chips page.
        _chips.NavigateToSelfRequested += async (_, _) => await OpenChipsAsync();
        // Same for the finish-read auto-read activity.
        _finishRead.NavigateToSelfRequested += async (_, _) => await OpenFinishReadAsync();
        // "Go to settings" on the online-results publish activity opens the Online-results page.
        _onlineResults.NavigateToSelfRequested += async (_, _) => await OpenOnlineResultsAsync();
        // Dashboard quick-action buttons route through here (the dashboard owns no page-open commands).
        _dashboard.QuickActionRequested += async (_, action) => await OnDashboardQuickActionAsync(action);
        _session.SessionChanged += OnSessionChanged;
        Localization.PropertyChanged += (_, _) => OnPropertyChanged(nameof(WindowTitle));

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

    /// <summary>Settings content shown in the global overlay.</summary>
    public SettingsViewModel Settings { get; }

    /// <summary>True while the working shell is active (enables the "change competition" item).</summary>
    public bool IsEventSelected => _session.HasSelection && !IsSettingsOpen;

    /// <summary>Navigation pages, surfaced as top-menu items once a competition is open.</summary>
    public ReadOnlyObservableCollection<PageViewModelBase> Pages { get; }

    /// <summary>
    /// Window title: the app name, plus the active competition once one is selected,
    /// e.g. "OrientDesk — City Championship 2026". The day is now a per-page concern.
    /// </summary>
    public string WindowTitle
    {
        get
        {
            var appName = Localization.Get("App.Title");
            if (!_session.HasSelection)
                return appName;

            var name = _session.CurrentEvent?.Name ?? string.Empty;
            return $"{appName} — {name}";
        }
    }

    [RelayCommand]
    private void Navigate(PageViewModelBase? page)
    {
        if (page is null)
            return;

        // Refresh the dashboard's live counts each time it is opened (data may have changed elsewhere).
        if (ReferenceEquals(page, _dashboard))
            _ = _dashboard.LoadAsync();

        _shell.SelectedPage = page;
    }

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
    private async Task OpenClassicDrawAsync()
    {
        await _classicDraw.LoadAsync();
        _shell.SelectedPage = _classicDraw;
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
        }
    }

    /// <summary>Called once after construction to restore the last session or show the picker.</summary>
    public async Task InitializeAsync()
    {
        // BD work runs off the UI thread inside RunAsync; UI-state writes happen after the await.
        var restored = await _busy.RunAsync(async () =>
        {
            await _uiScale.InitializeAsync();
            return await _session.TryRestoreLastAsync();
        });

        if (restored)
            ShowShell();
        else
            await ShowSelectionAsync();
    }

    [RelayCommand]
    private void OpenSettings() => IsSettingsOpen = true;

    [RelayCommand]
    private void CloseSettings() => IsSettingsOpen = false;

    private bool CanChangeEvent() => _session.HasSelection;

    [RelayCommand(CanExecute = nameof(CanChangeEvent))]
    private Task ChangeEventAsync() => ChangeEventInternalAsync();

    private async Task ChangeEventInternalAsync()
    {
        IsSettingsOpen = false;
        _session.Clear();
        await ShowSelectionAsync();
    }

    [RelayCommand]
    private void Exit()
    {
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

        ChangeEventCommand.NotifyCanExecuteChanged();
        OpenCompetitionInfoCommand.NotifyCanExecuteChanged();
        OpenCompetitionDaysCommand.NotifyCanExecuteChanged();
        OpenControlPointsCommand.NotifyCanExecuteChanged();
        OpenGroupsCommand.NotifyCanExecuteChanged();
        OpenChipsCommand.NotifyCanExecuteChanged();
        OpenFinishReadCommand.NotifyCanExecuteChanged();
        OpenParticipantsCommand.NotifyCanExecuteChanged();
        OpenRegionsCommand.NotifyCanExecuteChanged();
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
        OpenSplitsExportCommand.NotifyCanExecuteChanged();
        OpenDrawCommand.NotifyCanExecuteChanged();
        OpenClassicDrawCommand.NotifyCanExecuteChanged();
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

    private void ShowShell() => CurrentView = _shell;
}
