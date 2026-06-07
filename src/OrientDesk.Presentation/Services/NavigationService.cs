using System.Collections.ObjectModel;
using OrientDesk.Presentation.ViewModels.Pages;

namespace OrientDesk.Presentation.Services;

/// <summary>
/// Builds the page list from DI-resolved page view models and exposes the active page.
/// The first registered page (Dashboard) is the default.
/// </summary>
public sealed class NavigationService : INavigationService
{
    private readonly ObservableCollection<PageViewModelBase> _pages;

    public NavigationService(
        DashboardViewModel dashboard,
        ParticipantsViewModel participants,
        GroupsViewModel groups,
        CoursesViewModel courses,
        PunchImportViewModel punchImport,
        ResultsViewModel results,
        ChipRentalViewModel chipRental)
    {
        // Settings is global (top menu → overlay), so it is not a sidebar page.
        _pages =
        [
            dashboard,
            participants,
            groups,
            courses,
            punchImport,
            results,
            chipRental
        ];

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

        CurrentPage = page;
        CurrentPageChanged?.Invoke(this, EventArgs.Empty);
    }
}
