using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientDesk.BusinessLogic.Interfaces;
using OrientDesk.Localization;
using OrientDesk.Presentation.Services;
using OrientDesk.Presentation.ViewModels.Pages;

namespace OrientDesk.Presentation.ViewModels;

/// <summary>
/// The working shell shown once a competition + day is selected: sidebar navigation,
/// content area, and a header showing the current competition and day.
/// </summary>
public sealed partial class ShellViewModel : ViewModelBase
{
    private readonly INavigationService _navigation;
    private readonly ISessionService _session;

    [ObservableProperty]
    private PageViewModelBase? _selectedPage;

    public ShellViewModel(INavigationService navigation, ISessionService session, ILocalizationService localization)
    {
        _navigation = navigation;
        _session = session;
        Localization = localization;
        Pages = navigation.Pages;
        _selectedPage = navigation.CurrentPage;
    }

    public ILocalizationService Localization { get; }

    public ReadOnlyObservableCollection<PageViewModelBase> Pages { get; }

    /// <summary>Display name of the active competition (header, top-left).</summary>
    public string CurrentEventName => _session.CurrentEvent?.Name ?? string.Empty;

    /// <summary>Active day number, for the header.</summary>
    public int CurrentDayNumber => _session.CurrentDay?.Number ?? 0;

    /// <summary>Raised so the host (MainWindow) can return to the selection screen.</summary>
    public event EventHandler? ChangeEventRequested;

    public void RefreshSessionHeader()
    {
        OnPropertyChanged(nameof(CurrentEventName));
        OnPropertyChanged(nameof(CurrentDayNumber));
    }

    [RelayCommand]
    private void ChangeEvent() => ChangeEventRequested?.Invoke(this, EventArgs.Empty);

    partial void OnSelectedPageChanged(PageViewModelBase? value)
    {
        if (value is not null)
            _navigation.NavigateTo(value);
    }
}
