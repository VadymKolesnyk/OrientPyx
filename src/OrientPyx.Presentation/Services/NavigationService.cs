using System.Collections.ObjectModel;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.Presentation.ViewModels.Pages;

namespace OrientPyx.Presentation.Services;

/// <summary>
/// Builds the page list from DI-resolved page view models and exposes the active page.
/// The first registered page (Dashboard) is the default.
/// </summary>
public sealed class NavigationService : INavigationService
{
    private readonly ObservableCollection<PageViewModelBase> _pages;
    private readonly IActivityLog _log;

    public NavigationService(DashboardViewModel dashboard, IActivityLog log)
    {
        _log = log;
        // Settings is global (top menu → overlay), so it is not a sidebar page.
        _pages = [dashboard];

        Pages = new ReadOnlyObservableCollection<PageViewModelBase>(_pages);
        CurrentPage = _pages.FirstOrDefault();
    }

    public ReadOnlyObservableCollection<PageViewModelBase> Pages { get; }

    public PageViewModelBase? CurrentPage { get; private set; }

    public event EventHandler? CurrentPageChanged;

    public void NavigateTo(PageViewModelBase page)
    {
        if (ReferenceEquals(page, CurrentPage))
            return;

        _log.Action($"Navigate to page: {page.GetType().Name}");
        CurrentPage = page;
        CurrentPageChanged?.Invoke(this, EventArgs.Empty);
    }
}
