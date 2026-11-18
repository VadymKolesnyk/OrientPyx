using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Services;
using OrientPyx.BusinessLogic.Services.Network;
using OrientPyx.Localization;
using OrientPyx.Presentation.Services;
using OrientPyx.Presentation.Services.Undo;
using OrientPyx.BusinessLogic.Models;

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

    // ── Undo / redo (Ctrl+Z / Ctrl+Y, see IUndoHistory)
    // A page that edits competition data calls UseUndo in its constructor, captures a step when a value changes
    // (CaptureUndo — synchronously, at the change) and runs each write of it through RunUndoAsync. The history is per
    // page and per day picked on it (UndoDayId); the shell's keys and the «Правка» menu act on the shown page.

    /// <summary>The undo history, when this page supports undo (null otherwise: Ctrl+Z does nothing here).</summary>
    protected IUndoHistory? UndoHistory { get; private set; }

    /// <summary>Opts the page into undo. Call from the constructor.</summary>
    protected void UseUndo(IUndoHistory history) => UndoHistory = history;

    /// <summary>True when Ctrl+Z / Ctrl+Y act on this page.</summary>
    public bool SupportsUndo => UndoHistory is not null;

    /// <summary>The page part of the history key (default: the type name — one history per page type).</summary>
    public virtual string UndoKey => GetType().Name;

    /// <summary>The day the page edits right now (its picker's day); null for a page without days or a cross-day view.</summary>
    public virtual Guid? UndoDayId => null;

    /// <summary>The history the page's current edits go to.</summary>
    public UndoHistoryKey CurrentUndoKey => new(UndoKey, UndoDayId);

    /// <summary>
    /// The step of the change being made now (see <see cref="IUndoHistory.Capture(PageViewModelBase, string)"/>); null
    /// when the page has no undo or the change is program-made — its write then runs outside every step.
    /// </summary>
    protected UndoEntry? CaptureUndo(string label) => UndoHistory?.Capture(this, label);

    /// <summary>
    /// The same for an explicit history of this page — a change on a row whose own day is not the page's day right now
    /// (the rows on screen are still the previous day's while a day switch lands), or a dialog writing into one day.
    /// </summary>
    protected UndoEntry? CaptureUndo(UndoHistoryKey key, string label) => UndoHistory?.Capture(key, label);

    /// <summary>
    /// A step of its own for a page action (a toolbar command or hotkey, a dialog's result): unlike
    /// <see cref="CaptureUndo(string)"/> it never joins the edit session of a cell that happens to be live — the
    /// focused cell keeps its editor (and its session) under a dialog and while a hotkey runs. An open gesture still
    /// wins (nested gestures join the outer one).
    /// </summary>
    protected UndoEntry? CaptureUndoAction(string label) => CaptureUndoAction(CurrentUndoKey, label);

    /// <inheritdoc cref="CaptureUndoAction(string)"/>
    protected UndoEntry? CaptureUndoAction(UndoHistoryKey key, string label)
    {
        if (UndoHistory is not { } history)
            return null;
        using (UndoGesture.Begin(label))
            return history.Capture(key, label);
    }

    /// <summary>Runs one write of a captured step (null ⇒ outside every step).</summary>
    protected Task RunUndoAsync(UndoEntry? step, Func<Task> write)
        => UndoHistory is { } history ? history.RunAsync(step, write) : write();

    /// <inheritdoc cref="RunUndoAsync(UndoEntry?, Func{Task})"/>
    protected Task<T> RunUndoAsync<T>(UndoEntry? step, Func<Task<T>> write)
        => UndoHistory is { } history ? history.RunAsync(step, write) : write();

    /// <summary>Captures a step and runs its (single) write — for a change written at once.</summary>
    protected Task UndoableAsync(string label, Func<Task> write) => RunUndoAsync(CaptureUndo(label), write);

    /// <inheritdoc cref="UndoableAsync(string, Func{Task})"/>
    protected Task<T> UndoableAsync<T>(string label, Func<Task<T>> write) => RunUndoAsync(CaptureUndo(label), write);

    /// <summary>A step label «column — row» from a column header key and the row's name.</summary>
    protected string UndoLabel(string columnKey, string? row)
        => string.IsNullOrWhiteSpace(row)
            ? Localization.Get(columnKey)
            : string.Format(Localization.Get("Undo.Label.Cell"), Localization.Get(columnKey), row.Trim());

    /// <summary>
    /// Lets the page's pending (debounced) writes land before an undo / redo or a day switch. The default has none.
    /// </summary>
    public virtual Task FlushPendingWritesAsync() => Task.CompletedTask;

    /// <summary>Reloads the page right after an undo / redo, awaited under the busy overlay (§9.5 of the undo plan).</summary>
    public Task ReloadForUndoAsync()
    {
        // The revert's own change (DataArea.Any, published before the revert returns) also asked for a live reload a
        // second later. This reload reads after the revert, so it already covers it — and the late one could apply
        // rows it read just before an edit the operator makes right after Ctrl+Z, showing that edit reverted (§9.5).
        // Only a live reload already in flight (it may have read before the revert) is followed by one more.
        lock (_pendingGate)
        {
            _pendingCompetitions.Clear();
            _pendingForAll = _reloading;
            _reloadPending = _reloading;
        }
        if (!_reloading)
            _reloadTimer?.Stop();
        return ReloadAsync();
    }

    /// <summary>Called after an undo / redo was applied (and the page reloaded): a page may select the rows it touched.</summary>
    public virtual void OnUndoApplied(UndoRevertResult result)
    {
    }

    // ── Live data (the change bus)
    // A page that shows competition data calls ObserveData(areas) in its constructor and overrides ReloadAsync with
    // its load path. While the page is shown (IsActive, set by the shell), a change in one of its areas — a write by
    // another station over the network, or by the host — reloads it: coalesced to at most once a second, deferred
    // while a cell is being edited, and skipped for this machine's own writes (the page already updated itself).

    private static readonly TimeSpan ReloadCoalesce = TimeSpan.FromSeconds(1);

    private IDataChangeBus? _bus;
    private ISessionService? _observedSession;
    private IWorkspaceMode? _mode;
    private DataArea[] _areas = [];
    private DispatcherTimer? _reloadTimer;
    private bool _reloadPending;
    private bool _reloading;
    private bool _isActive;

    /// <summary>True while this page is the one shown in the shell (the shell sets it).</summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value)
                return;
            _isActive = value;
            OnPropertyChanged(nameof(IsActive));
            if (value && _reloadPending)
                ScheduleReload();
        }
    }

    /// <summary>
    /// True while a cell editor on this page has focus; a bus-driven reload waits until it is released so the
    /// operator's typing is never replaced under their fingers. Views bind their table's editing state here.
    /// </summary>
    public bool IsEditingCell
    {
        get => _isEditingCell;
        set
        {
            if (_isEditingCell == value)
                return;
            _isEditingCell = value;
            OnPropertyChanged(nameof(IsEditingCell));
            if (!value && _reloadPending)
                ScheduleReload();
        }
    }

    private bool _isEditingCell;

    /// <summary>
    /// Tells whether the cell being edited holds a change the operator has not left yet (the view sets it from its
    /// tables). A cell merely focused — the editor goes live on focus — must not hold a reload back forever; only
    /// typing in progress does. Null ⇒ any live editor counts.
    /// </summary>
    public Func<bool>? UncommittedEditCheck { get; set; }

    // True while a reload would replace a value the operator is in the middle of typing.
    private bool EditInProgress => IsEditingCell && (UncommittedEditCheck?.Invoke() ?? true);

    /// <summary>
    /// Subscribes the page to the change bus for the given areas (plus <see cref="DataArea.Any"/>, which every page
    /// honours). Call once from the constructor of a page that overrides <see cref="ReloadAsync"/>.
    /// </summary>
    protected void ObserveData(IDataChangeBus bus, ISessionService session, IWorkspaceMode mode, params DataArea[] areas)
    {
        _bus = bus;
        _observedSession = session;
        _mode = mode;
        _areas = areas;
        bus.Changed += OnDataChanged;
    }

    /// <summary>
    /// Reloads the page's content from the store after data changed elsewhere. The default does nothing; a page moves
    /// its load path here (what it does on <c>SessionChanged</c> / when opened). It runs on the UI thread. It should
    /// keep the operator's state — filters, sort, column view, selected row — rather than resetting the page.
    /// </summary>
    protected virtual Task ReloadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>Asks for a reload as if the bus had raised a change (e.g. after reconnecting to the host).</summary>
    public void RequestReload()
    {
        lock (_pendingGate)
            _pendingForAll = true;
        _reloadPending = true;
        if (Dispatcher.UIThread.CheckAccess())
            ScheduleReload();
        else
            Dispatcher.UIThread.Post(ScheduleReload);
    }

    private void OnDataChanged(object? sender, DataChange change)
    {
        if (change.Area != DataArea.Any && change.Area != DataArea.AppData && Array.IndexOf(_areas, change.Area) < 0)
            return;
        if (change.Area == DataArea.AppData && Array.IndexOf(_areas, DataArea.AppData) < 0)
            return;
        // Our own writes: the page that made them has already refreshed itself.
        if (change.Area != DataArea.Any && _mode is not null
            && string.Equals(change.Origin, _mode.SelfOrigin, StringComparison.Ordinal))
            return;
        // Another competition's change is irrelevant. App-data concerns every competition, and so does «any» when it
        // names none (a full refresh after reconnecting); «any» for one competition (an undo) concerns that one only.
        var competition = change.Area == DataArea.AppData || change.EventFolderPath == DataChangeBus.AnyEvent
            ? null
            : change.EventFolderPath;
        if (competition is not null
            && _observedSession?.CurrentEvent is { } ev
            && !SameCompetition(competition, ev.FolderPath))
            return;

        lock (_pendingGate)
        {
            if (competition is null)
                _pendingForAll = true;
            else if (!_pendingForAll)
                _pendingCompetitions.Add(competition);
            _reloadPending = true;
        }
        Dispatcher.UIThread.Post(ScheduleReload);
    }

    // The competitions a pending reload is for (or all of them). A page hidden while another competition was open keeps
    // its reload pending until it is shown; by then another competition may be open — which the page loaded anew on the
    // session change — so a reload for none of the open one is dropped instead of re-reading the page mid-way through
    // what the operator is doing (an undo publishes «any» for its competition, so every hidden page gets one).
    private readonly object _pendingGate = new();
    private readonly HashSet<string> _pendingCompetitions = new(StringComparer.OrdinalIgnoreCase);
    private bool _pendingForAll;

    // The event path is the host's folder on a host and the bare identifier on a station; the session's FolderPath
    // follows the same convention, but the two can differ in form (a trailing separator, relative vs absolute), so
    // compare by the folder name — the identifier — which is what both end in.
    private static bool SameCompetition(string eventPath, string sessionPath)
    {
        static string Key(string path) => System.IO.Path.GetFileName(path.TrimEnd('/', '\\'));
        return string.Equals(eventPath, sessionPath, StringComparison.OrdinalIgnoreCase)
               || string.Equals(Key(eventPath), Key(sessionPath), StringComparison.OrdinalIgnoreCase);
    }

    // On the UI thread. Starts (or lets run) the coalescing timer; the reload itself fires when it elapses. While a
    // cell edit is in progress the timer keeps ticking, so the reload lands as soon as the edit is committed.
    private void ScheduleReload()
    {
        if (!_reloadPending || !IsActive)
            return;
        if (_reloadTimer is null)
        {
            _reloadTimer = new DispatcherTimer { Interval = ReloadCoalesce };
            _reloadTimer.Tick += async (_, _) =>
            {
                _reloadTimer.Stop();
                await RunReloadAsync();
            };
        }
        if (!_reloadTimer.IsEnabled)
            _reloadTimer.Start();
    }

    private async Task RunReloadAsync()
    {
        if (!_reloadPending || !IsActive)
            return;
        if (EditInProgress)
        {
            // Try again in a second — the operator is typing; the edit is usually over by then.
            _reloadTimer?.Start();
            return;
        }
        if (_reloading)
        {
            // A reload is in flight; the pending flag stays set and the timer re-arms when it finishes.
            return;
        }
        bool stale;
        lock (_pendingGate)
        {
            stale = !_pendingForAll
                    && _pendingCompetitions.Count > 0
                    && _observedSession?.CurrentEvent is { } current
                    && !_pendingCompetitions.Any(c => SameCompetition(c, current.FolderPath));
            _pendingForAll = false;
            _pendingCompetitions.Clear();
            _reloadPending = false;
        }
        if (stale)
            return;
        _reloading = true;
        try
        {
            // Silent: the busy service shows no overlay for a refresh caused by another machine's write.
            using (BackgroundRefresh.Scope())
                await ReloadAsync();
        }
        catch (Exception)
        {
            // A failed live refresh is not fatal: the next change (or the next open) reloads again. The busy
            // service has already logged a store failure.
        }
        finally
        {
            _reloading = false;
        }
        if (_reloadPending)
            ScheduleReload();
    }
}
