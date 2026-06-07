using System.Collections.ObjectModel;
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

    private readonly CompetitionInfoViewModel _competitionInfo;
    private readonly CompetitionDaysViewModel _competitionDays;

    public MainWindowViewModel(
        ISessionService session,
        INavigationService navigation,
        EventSelectionViewModel selection,
        CreateEventViewModel create,
        ShellViewModel shell,
        SettingsViewModel settings,
        CompetitionInfoViewModel competitionInfo,
        CompetitionDaysViewModel competitionDays,
        ILocalizationService localization,
        IUiScaleService uiScale,
        IBusyService busy)
    {
        _session = session;
        _navigation = navigation;
        _selection = selection;
        _create = create;
        _shell = shell;
        Settings = settings;
        _competitionInfo = competitionInfo;
        _competitionDays = competitionDays;
        Localization = localization;
        _uiScale = uiScale;
        _busy = busy;

        Pages = navigation.Pages;

        _selection.CreateRequested += (_, _) => ShowCreate();
        // Runs inside the create operation's busy scope, so the loader stays up until the list is ready.
        _create.OnCreatedAsync = async _ => await LoadSelectionAsync();
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

    /// <summary>Settings content shown in the global overlay.</summary>
    public SettingsViewModel Settings { get; }

    /// <summary>True while the working shell is active (enables the "change competition" item).</summary>
    public bool IsEventSelected => _session.HasSelection && !IsSettingsOpen;

    /// <summary>Navigation pages, surfaced as top-menu items once a competition is open.</summary>
    public ReadOnlyObservableCollection<PageViewModelBase> Pages { get; }

    /// <summary>
    /// Window title: the app name, plus the active competition and day once one is selected,
    /// e.g. "OrientDesk — City Championship 2026 (Day 1)".
    /// </summary>
    public string WindowTitle
    {
        get
        {
            var appName = Localization.Get("App.Title");
            if (!_session.HasSelection)
                return appName;

            var name = _session.CurrentEvent?.Name ?? string.Empty;
            var dayLabel = Localization.Get("Header.Day");
            var day = _session.CurrentDay?.Number ?? 0;
            return $"{appName} — {name} ({dayLabel} {day})";
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

    /// <summary>Called once after construction to restore the last session or show the picker.</summary>
    public async Task InitializeAsync()
    {
        await _busy.RunAsync(async () =>
        {
            await _uiScale.InitializeAsync();
            var restored = await _session.TryRestoreLastAsync();
            if (restored)
                ShowShell();
            else
                await LoadSelectionAsync();
        });
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
        ChangeEventCommand.NotifyCanExecuteChanged();
        OpenCompetitionInfoCommand.NotifyCanExecuteChanged();
        OpenCompetitionDaysCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsEventSelected));
        OnPropertyChanged(nameof(WindowTitle));

        if (_session.HasSelection)
        {
            _shell.RefreshSessionHeader();
            ShowShell();
        }
    }

    partial void OnIsSettingsOpenChanged(bool value) => OnPropertyChanged(nameof(IsEventSelected));

    private Task ShowSelectionAsync() => _busy.RunAsync(LoadSelectionAsync);

    private async Task LoadSelectionAsync()
    {
        await _selection.LoadAsync();
        CurrentView = _selection;
    }

    private void ShowCreate()
    {
        _create.Reset();
        CurrentView = _create;
    }

    private void ShowShell() => CurrentView = _shell;
}
