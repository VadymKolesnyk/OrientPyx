using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.Localization;
using OrientPyx.Presentation.Services;
using OrientPyx.Presentation.ViewModels.Pages;

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

    public ShellViewModel(INavigationService navigation, ILocalizationService localization)
    {
        _navigation = navigation;
        Localization = localization;
        Pages = navigation.Pages;
        _selectedPage = navigation.CurrentPage;
    }

    public ILocalizationService Localization { get; }

    public ReadOnlyObservableCollection<PageViewModelBase> Pages { get; }

    /// <summary>Raised so the host (MainWindow) can return to the selection screen.</summary>
    public event EventHandler? ChangeEventRequested;

    [RelayCommand]
    private void ChangeEvent() => ChangeEventRequested?.Invoke(this, EventArgs.Empty);

    partial void OnSelectedPageChanged(PageViewModelBase? value)
    {
        if (value is not null)
            _navigation.NavigateTo(value);
    }
}
