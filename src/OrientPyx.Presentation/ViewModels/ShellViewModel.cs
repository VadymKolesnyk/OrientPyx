using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.Localization;
using OrientPyx.Presentation.Services;
using OrientPyx.Presentation.ViewModels.Pages;
using OrientPyx.Presentation.ViewModels.Shared;

namespace OrientPyx.Presentation.ViewModels;

/// <summary>
/// The working shell shown once a competition is selected: sidebar navigation and the
/// content area. The active day is a per-page concern, not part of the shell.
/// </summary>
public sealed partial class ShellViewModel : ViewModelBase
{
    private readonly INavigationService _navigation;

    [ObservableProperty]
    private PageViewModelBase? _selectedPage;

    public ShellViewModel(
        INavigationService navigation,
        ILocalizationService localization,
        PluginNoticeViewModel pluginNotices,
        NetworkNoticeViewModel networkNotices)
    {
        _navigation = navigation;
        PluginNotices = pluginNotices;
        NetworkNotices = networkNotices;
        Localization = localization;
        Pages = navigation.Pages;
        _selectedPage = navigation.CurrentPage;
    }

    public ILocalizationService Localization { get; }

    /// <summary>The «Потрібен плагін» / plugin-failure banners above the page.</summary>
    public PluginNoticeViewModel PluginNotices { get; }

    /// <summary>The «Немає зв'язку з хостом» banner; while it is up the page content is disabled.</summary>
    public NetworkNoticeViewModel NetworkNotices { get; }

    public ReadOnlyObservableCollection<PageViewModelBase> Pages { get; }

    /// <summary>Raised so the host (MainWindow) can return to the selection screen.</summary>
    public event EventHandler? ChangeEventRequested;

    [RelayCommand]
    private void ChangeEvent() => ChangeEventRequested?.Invoke(this, EventArgs.Empty);

    partial void OnSelectedPageChanged(PageViewModelBase? oldValue, PageViewModelBase? newValue)
    {
        // Only the shown page follows the change bus (see PageViewModelBase.ObserveData).
        if (oldValue is not null)
            oldValue.IsActive = false;
        if (newValue is not null)
        {
            newValue.IsActive = true;
            _navigation.NavigateTo(newValue);
        }
    }
}
