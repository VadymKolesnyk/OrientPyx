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

    private readonly CompetitionInfoViewModel _competitionInfo;
    private readonly CompetitionDaysViewModel _competitionDays;
    private readonly ControlPointsViewModel _controlPoints;
    private readonly GroupsViewModel _groups;
    private readonly ChipsViewModel _chips;

    public MainWindowViewModel(
        ISessionService session,
        INavigationService navigation,
        EventSelectionViewModel selection,
        CreateEventViewModel create,
        ShellViewModel shell,
        SettingsViewModel settings,
        CompetitionInfoViewModel competitionInfo,
        CompetitionDaysViewModel competitionDays,
        ControlPointsViewModel controlPoints,
        GroupsViewModel groups,
        ChipsViewModel chips,
        ILocalizationService localization,
        IUiScaleService uiScale,
        IBusyService busy,
        IDialogService dialogs)
    {
        _session = session;
        _navigation = navigation;
        _selection = selection;
        _create = create;
        _shell = shell;
        Settings = settings;
        _competitionInfo = competitionInfo;
        _competitionDays = competitionDays;
        _controlPoints = controlPoints;
        _groups = groups;
        _chips = chips;
        Localization = localization;
        _uiScale = uiScale;
        _busy = busy;
        _dialogs = dialogs;

        Pages = navigation.Pages;

        _selection.CreateRequested += (_, _) => ShowCreate();
        // Runs inside the create operation's busy scope, so the loader stays up until the list is ready.
        _create.OnCreatedAsync = async _ => await ShowSelectionAsync();
        _create.Cancelled += async (_, _) => await ShowSelectionAsync();
        _shell.ChangeEventRequested += async (_, _) => await ChangeEventInternalAsync();
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
        if (page is not null)
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
