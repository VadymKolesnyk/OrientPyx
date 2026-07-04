using System.Collections.ObjectModel;
using OrientPyx.Presentation.ViewModels.Pages;

namespace OrientPyx.Presentation.Services;

/// <summary>Owns the set of navigable pages and tracks the current one.</summary>
public interface INavigationService
{
    /// <summary>All pages shown in the sidebar, in order.</summary>
    ReadOnlyObservableCollection<PageViewModelBase> Pages { get; }

    PageViewModelBase? CurrentPage { get; }

    event EventHandler? CurrentPageChanged;

    void NavigateTo(PageViewModelBase page);
}
