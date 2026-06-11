using System.ComponentModel;
using OrientDesk.Localization;

namespace OrientDesk.Presentation.ViewModels.Pages;

/// <summary>
/// Base for navigable pages. Holds localization keys (never raw UI text) and exposes
/// resolved display strings that refresh automatically when the language changes.
/// </summary>
public abstract class PageViewModelBase : ViewModelBase
{
    protected PageViewModelBase(ILocalizationService localization)
    {
        Localization = localization;
        Localization.PropertyChanged += OnLocalizationChanged;
    }

    /// <summary>Exposed so Views can also bind literal keys: {Binding Localization[App.Title]}.</summary>
    public ILocalizationService Localization { get; }

    /// <summary>Localization key for the sidebar navigation label.</summary>
    public abstract string NavKey { get; }

    /// <summary>Localization key for the page title.</summary>
    public abstract string TitleKey { get; }

    /// <summary>Localization key for the page placeholder text.</summary>
    public abstract string TextKey { get; }

    /// <summary>
    /// Path geometry for the page's icon (shown on placeholder pages). Defaults to a generic
    /// document glyph; pages override it with something representative.
    /// </summary>
    public virtual string IconData =>
        "M6,2 h8 l4,4 v14 a1,1 0 0 1 -1,1 h-11 a1,1 0 0 1 -1,-1 v-17 a1,1 0 0 1 1,-1 z M14,2 v4 h4";

    public string NavLabel => Localization.Get(NavKey);
    public string Title => Localization.Get(TitleKey);
    public string Text => Localization.Get(TextKey);

    /// <summary>
    /// Raised when the page wants keyboard focus moved back onto its main grid. Showing a modal
    /// dialog (e.g. the delete confirmation) hands focus to the overlay; once it closes, focus does
    /// not return to the grid on its own and lands on the top menu instead. The View handles this
    /// by focusing its grid. Only fired after a path that opened a dialog — direct (no-confirm)
    /// deletes never lose grid focus.
    /// </summary>
    public event EventHandler? FocusGridRequested;

    /// <summary>Asks the View to return keyboard focus to the page's grid (see <see cref="FocusGridRequested"/>).</summary>
    protected void RequestGridFocus() => FocusGridRequested?.Invoke(this, EventArgs.Empty);

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Language switched — re-evaluate all resolved strings.
        OnPropertyChanged(nameof(NavLabel));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Text));
    }
}
