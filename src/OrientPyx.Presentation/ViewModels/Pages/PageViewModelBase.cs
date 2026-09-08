using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.Localization;
using OrientPyx.Presentation.Services;

namespace OrientPyx.Presentation.ViewModels.Pages;

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
        ShowHelpCommand = new RelayCommand(() => HelpRequested?.Invoke(this, EventArgs.Empty));
        ShowBlockHelpCommand = new RelayCommand<string>(prefix =>
        {
            if (!string.IsNullOrWhiteSpace(prefix))
                BlockHelpRequested?.Invoke(this, prefix);
        });
    }

    /// <summary>Exposed so Views can also bind literal keys: {Binding Localization[App.Title]}.</summary>
    public ILocalizationService Localization { get; }

    /// <summary>Localization key for the sidebar navigation label.</summary>
    public abstract string NavKey { get; }

    public abstract string TitleKey { get; }

    /// <summary>Localization key for the page placeholder text.</summary>
    public abstract string TextKey { get; }

    /// <summary>
    /// Path geometry for the page's icon (shown on placeholder pages). Defaults to a generic
    /// document glyph; pages override it with something representative.
    /// </summary>
    // Lucide "file-text" (see OrientPyx.Presentation.Controls.IconData). Rendered stroked by controls:Icon.
    public virtual string IconData =>
        "M15 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V7Z M14 2v4a2 2 0 0 0 2 2h4 M16 13H8 M16 17H8 M10 9H8";

    public string NavLabel => Localization.Get(NavKey);
    public string Title => Localization.Get(TitleKey);
    public string Text => Localization.Get(TextKey);

    /// <summary>
    /// Localization key prefix for this screen's «?» help modal. The dialog resolves
    /// <c>{prefix}.Title/.What/.Why/.How</c>. Defaults to a prefix derived from the page's class
    /// name (<c>Help.{Name-without-ViewModel}</c>); a page may override it if its class name and
    /// help keys differ.
    /// </summary>
    public virtual string HelpKeyPrefix =>
        $"Help.{GetType().Name.Replace("ViewModel", string.Empty)}";

    /// <summary>
    /// Raised when the user clicks the page's «?» button. Handled by the root
    /// <see cref="MainWindowViewModel"/> (which owns the dialog service) to open the help modal —
    /// same event-based wiring as <see cref="FocusGridRequested"/>, so the base needs no dialog
    /// dependency of its own.
    /// </summary>
    public event EventHandler? HelpRequested;

    /// <summary>Opens this screen's help modal (see <see cref="HelpRequested"/>).</summary>
    public System.Windows.Input.ICommand ShowHelpCommand { get; }

    /// <summary>
    /// Raised when the user clicks a per-block «?» button on a multi-section page, carrying that
    /// block's own help key prefix (e.g. <c>Help.EntryFees.Chips</c>). Handled by
    /// <see cref="MainWindowViewModel"/> exactly like <see cref="HelpRequested"/>, but the dialog
    /// resolves <c>{prefix}.Title/.What/.Why/.How</c> from the supplied prefix instead of the page's
    /// own <see cref="HelpKeyPrefix"/>. Lets any page put a focused help modal next to each section.
    /// </summary>
    public event EventHandler<string>? BlockHelpRequested;

    /// <summary>
    /// Opens a section-specific help modal for the given key prefix (see <see cref="BlockHelpRequested"/>).
    /// Bind a block's «?» button to this with <c>CommandParameter</c> set to the prefix.
    /// </summary>
    public System.Windows.Input.ICommand ShowBlockHelpCommand { get; }

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

    // ── Day lock (see IDayLockService)
    // Per-day pages hand the base their lock service; the shared day selector then binds straight to
    // these. A page that never writes to a day simply leaves the service null and shows no lock.

    private IDayLockService? _dayLock;

    /// <summary>
    /// Wires this page to the day lock. Call from the constructor of a page that edits day data; the
    /// day selector picks the state up from <see cref="IsDayLocked"/> and <see cref="ToggleDayLockCommand"/>.
    /// </summary>
    protected void UseDayLock(IDayLockService dayLock)
    {
        _dayLock = dayLock;
        ToggleDayLockCommand = new AsyncRelayCommand(ToggleDayLockAsync);
    }

    /// <summary>
    /// True when the page's current day is closed for editing (false when it has no lock). Virtual for
    /// the same reason as <see cref="CanToggleDayLock"/>: a view that isn't bound to one day must not
    /// report the session day's state as its own.
    /// </summary>
    public virtual bool IsDayLocked => _dayLock?.IsCurrentDayLocked ?? false;

    /// <summary>
    /// True on pages that write to the day, so the selector offers the lock button. Virtual because a
    /// page may show views that aren't bound to one day — the participants roster spans every day at
    /// once, where a single day's lock has nothing to act on.
    /// </summary>
    public virtual bool CanToggleDayLock => _dayLock is not null;

    /// <summary>Bound by the day selector's lock button; null on pages with no lock.</summary>
    public IAsyncRelayCommand? ToggleDayLockCommand { get; private set; }

    /// <summary>
    /// Re-raises the lock-derived properties. Pages call this from their load path so the lock state
    /// shown next to the day (and every cell's editability) follows a day switch.
    /// </summary>
    protected void RefreshDayLock()
    {
        OnPropertyChanged(nameof(IsDayLocked));
        OnPropertyChanged(nameof(CanToggleDayLock));
    }

    // Flipping the lock rewrites the session's current day, and that raises SessionChanged — which every
    // per-day page already handles by reloading. So there is nothing to re-fire here: the reload carries
    // the new state into every row, and RefreshDayLock covers the selector itself.
    /// <summary>
    /// Explains a read-only cell the user just clicked into (see <c>SheetTable.LockedEditAttempted</c>).
    /// Views wire their table's event to this so the lock never reads as the app ignoring a click.
    /// </summary>
    /// <param name="dayNumber">The closed day the cell belongs to; 0 means "the page's current day".</param>
    /// <param name="merged">True for a roster cell that writes to several days at once.</param>
    /// <remarks>
    /// Virtual because a page may hold the lock service without going through <see cref="UseDayLock"/> —
    /// the days grid shows every day at once, so it explains a row's own day rather than the session's.
    /// </remarks>
    public virtual Task ExplainDayLockAsync(int dayNumber = 0, bool merged = false)
        => _dayLock?.ExplainLockedCellAsync(dayNumber, merged) ?? Task.CompletedTask;

    /// <summary>
    /// The gate every action that writes to the day passes through first: returns false (and explains
    /// why) when the day is closed. Cells are covered by the table's own lock, but a toolbar action —
    /// an import, a draw, a bulk edit, clearing the read-out log — starts from a button, so each of
    /// those commands calls this before doing anything.
    ///
    /// Buttons are also greyed out while the day is closed; this is what makes the rule hold for the
    /// paths a disabled button can't cover (a hotkey, a flyout item, a command invoked in code).
    /// </summary>
    public virtual async Task<bool> EnsureDayEditableForActionAsync()
    {
        if (!IsDayLocked)
            return true;

        await ExplainDayLockAsync();
        return false;
    }

    /// <summary>Same gate, for commands on the view model itself.</summary>
    protected Task<bool> EnsureDayEditableAsync() => EnsureDayEditableForActionAsync();

    // Guards against a second click landing while the first toggle is still in flight. The toggle
    // awaits a confirmation dialog and a database write, and the button stays clickable throughout —
    // without this, two overlapping runs would each decide the direction from the same starting state
    // and the day would end up where it began.
    private bool _togglingDayLock;

    private async Task ToggleDayLockAsync()
    {
        if (_dayLock is null || _togglingDayLock)
            return;

        _togglingDayLock = true;
        try
        {
            await _dayLock.ToggleCurrentDayAsync();
        }
        finally
        {
            _togglingDayLock = false;
        }

        RefreshDayLock();
    }

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Language switched — re-evaluate all resolved strings.
        OnPropertyChanged(nameof(NavLabel));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Text));
    }
}
