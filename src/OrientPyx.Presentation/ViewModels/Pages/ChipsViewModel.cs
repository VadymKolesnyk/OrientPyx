using System.Collections.ObjectModel;
using System.IO;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.Localization;
using OrientPyx.Presentation.Services;
using OrientPyx.Presentation.ViewModels.Dialogs;

namespace OrientPyx.Presentation.ViewModels.Pages;

/// <summary>
/// Spreadsheet-like rental-chip database for the CURRENT competition (chips are competition-level,
/// issued for all days, so there is no day picker). Cells auto-save in the background (debounced per
/// row) like the other grid pages. Chips can be added one-by-one, in a numbered range (bulk modal),
/// or picked up automatically from a watched readout file every N seconds (stamped with a configurable note).
/// </summary>
public sealed partial class ChipsViewModel : PageViewModelBase
{
    private static readonly TimeSpan SaveDebounce = TimeSpan.FromMilliseconds(600);

    private readonly ICompetitionEditorService _editor;
    private readonly ISessionService _session;
    private readonly IReadoutParserSelector _readoutParsers;
    private readonly IFileReadoutPoller _poller;
    private readonly IBusyService _busy;
    private readonly IDialogService _dialogs;
    private readonly IBackgroundActivityService _activities;
    private readonly Dictionary<Guid, CancellationTokenSource> _saveTimers = new();

    /// <summary>
    /// The top-bar background-activity handle for auto-read, created while it is active. Lets the user
    /// pause/stop/jump-here from the menu bar without opening this page. Null when auto-read is off.
    /// </summary>
    private ChipAutoReadActivity? _autoReadActivity;

    public ChipsViewModel(
        ILocalizationService localization,
        ICompetitionEditorService editor,
        ISessionService session,
        IReadoutParserSelector readoutParsers,
        IFileReadoutPoller poller,
        IBusyService busy,
        IDialogService dialogs,
        IBackgroundActivityService activities,
        ITableLayoutStore layoutStore)
        : base(localization)
    {
        LayoutStore = layoutStore;
        _editor = editor;
        _session = session;
        _readoutParsers = readoutParsers;
        _poller = poller;
        _busy = busy;
        _dialogs = dialogs;
        _activities = activities;

        // Singleton VM: when the competition changes, drop the previous event's rows and stop any
        // watch (the file belongs to the old event). The event can be raised on a pool thread.
        _session.SessionChanged += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            StopAutoRead();
            _ = LoadAsync();
        });
    }

    /// <summary>Per-competition table-view store; persists this page's table column order/width/visibility.</summary>
    public ITableLayoutStore LayoutStore { get; }

    public override string NavKey => "Nav.Chips";
    public override string TitleKey => "Page.Chips.Title";
    public override string TextKey => "Page.Chips.Text";

    /// <summary>
    /// Raised when the user clicks "go to settings" on the top-bar auto-read activity, asking the host
    /// to bring this page into view. The shell, not the page, owns navigation, so the page only signals.
    /// </summary>
    public event EventHandler? NavigateToSelfRequested;

    public ObservableCollection<RentalChipRowViewModel> Chips { get; } = [];

    /// <summary>The row selected in the grid; the Delete key acts on it.</summary>
    [ObservableProperty]
    private RentalChipRowViewModel? _selectedChip;

    // --- Auto-read panel (in-memory only; never persisted, per the session rule) -----------------

    /// <summary>Default readout file: <c>chips\rentchip.csv</c> under the competition folder.</summary>
    private const string DefaultReadoutSubPath = "chips/rentchip.csv";

    /// <summary>Whether the auto-read card is expanded (collapsible to save vertical space).</summary>
    [ObservableProperty]
    private bool _isAutoReadExpanded;

    /// <summary>Path of the readout file to watch. Set via the picker or typed directly.</summary>
    [ObservableProperty]
    private string _autoReadFilePath = string.Empty;

    /// <summary>Poll interval in seconds. Bound to a +/- stepper (floored at 1s by the poller).</summary>
    [ObservableProperty]
    private int _autoReadIntervalSeconds = 5;

    /// <summary>When true, the watched file is read every N seconds and new chips are added silently.</summary>
    [ObservableProperty]
    private bool _autoReadEnabled;

    /// <summary>
    /// True while auto-read is on but paused from the top-bar activity block: the toggle stays "enabled"
    /// (settings kept) yet the poller is stopped until the user resumes. The page reflects this so the
    /// in-page UI and the menu-bar block never disagree.
    /// </summary>
    [ObservableProperty]
    private bool _isAutoReadPaused;

    /// <summary>Note stamped on each chip picked up by auto-read (e.g. who is reading them in).</summary>
    [ObservableProperty]
    private string _autoReadNote = string.Empty;

    [RelayCommand]
    private void ToggleAutoRead() => IsAutoReadExpanded = !IsAutoReadExpanded;

    /// <summary>Reloads the chips for the current competition. Called when the page is shown.</summary>
    public async Task LoadAsync()
    {
        CancelAllTimers();

        // Pre-fill the watch path with the per-competition default (chips/rentchip.csv). Done before
        // the await so it lands even when the page is first shown; the file itself is only created
        // when auto-read is switched on. Leave a user-customised path alone.
        var folder = _session.CurrentEvent?.FolderPath;
        if (!string.IsNullOrEmpty(folder) && string.IsNullOrWhiteSpace(AutoReadFilePath))
            AutoReadFilePath = Path.Combine(folder, DefaultReadoutSubPath.Replace('/', Path.DirectorySeparatorChar));

        var chips = await _busy.RunAsync(() => _editor.GetRentalChipsAsync());
        // Who holds each chip (across all days) — populates the read-only "assigned to" column.
        var holders = await _busy.RunAsync(() => _editor.GetRentalChipHoldersAsync());

        foreach (var existing in Chips)
            existing.PropertyChanged -= OnRowPropertyChanged;
        Chips.Clear();

        foreach (var chip in chips)
        {
            var row = CreateRow(chip);
            if (holders.TryGetValue(chip.Number.Trim(), out var names))
                row.AssignedTo = names;
            Chips.Add(row);
        }

        RefreshDuplicates();
    }

    // Flags every row whose number collides with another chip (numbers must be unique per competition).
    // Duplicates are never saved — UpdateRentalChipAsync reverts a colliding edit — but the DB revert is
    // silent, so this drives a red cell tint that tells the user the number can't be used. Recomputed on
    // load and after each number edit; blank numbers never count as duplicates.
    private void RefreshDuplicates()
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Chips)
        {
            var number = (row.Number ?? string.Empty).Trim();
            if (number.Length == 0)
                continue;
            counts[number] = counts.TryGetValue(number, out var n) ? n + 1 : 1;
        }

        foreach (var row in Chips)
        {
            var number = (row.Number ?? string.Empty).Trim();
            row.IsDuplicate = number.Length > 0 && counts.TryGetValue(number, out var n) && n > 1;
        }
    }

    private RentalChipRowViewModel CreateRow(RentalChip chip)
    {
        var vm = new RentalChipRowViewModel(chip, Localization, RequestRowSave);
        vm.PropertyChanged += OnRowPropertyChanged;
        return vm;
    }

    // Number edits can change row order (the store sorts by number), but reordering live would be
    // jarring mid-edit; the page simply re-sorts on its next reload. A number change can, however, create
    // or clear a duplicate, so re-flag collisions live for the red cell tint. (Ignore our own IsDuplicate
    // write to avoid re-entering.)
    private void OnRowPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RentalChipRowViewModel.Number))
            RefreshDuplicates();
    }

    [RelayCommand]
    private async Task AddChipAsync()
    {
        if (_session.CurrentEvent is null)
            return;

        // Persist immediately so the new row carries its real id for later debounced updates.
        var chip = await _busy.RunAsync(() => _editor.AddRentalChipAsync());
        Chips.Add(CreateRow(chip));
    }

    [RelayCommand]
    private async Task BulkAddAsync()
    {
        if (_session.CurrentEvent is null)
            return;

        var dialog = new BulkAddChipsViewModel(Localization);
        var result = await _dialogs.ShowBulkAddChipsAsync(dialog);
        if (result is null)
            return;

        await _busy.RunAsync(() =>
            _editor.AddRentalChipRangeAsync(result.StartNumber, result.Count, result.Note));
        await LoadAsync();
    }

    // Wipes the whole rental-chip database for the current competition after a confirmation. Cancels
    // any pending row saves first so a debounced update can't resurrect a row mid-clear.
    [RelayCommand]
    private async Task ClearChipsAsync()
    {
        if (_session.CurrentEvent is null || Chips.Count == 0)
            return;

        var confirmed = await _dialogs.ConfirmAsync(new ConfirmDialogViewModel(
            Localization,
            titleKey: "Chips.Clear.ConfirmTitle",
            messageKey: "Chips.Clear.ConfirmMessage"));
        if (!confirmed)
            return;

        CancelAllTimers();
        await _busy.RunAsync(() => _editor.ClearRentalChipsAsync());
        await LoadAsync();
    }

    // The grid's delete button binds to this command. A plain click confirms first; Ctrl+Click and
    // the Delete key route through the no-confirm/selected variants below.
    [RelayCommand]
    private Task DeleteChipAsync(RentalChipRowViewModel? row) => RemoveChipAsync(row, skipConfirm: false);

    /// <summary>Deletes a row without the confirmation prompt (Ctrl+Click / Ctrl+Delete).</summary>
    public Task DeleteChipNoConfirmAsync(RentalChipRowViewModel? row) => RemoveChipAsync(row, skipConfirm: true);

    /// <summary>Deletes the currently selected chip (Delete key); confirms unless skipConfirm.</summary>
    public Task DeleteSelectedChipAsync(bool skipConfirm) => RemoveChipAsync(SelectedChip, skipConfirm);

    private async Task RemoveChipAsync(RentalChipRowViewModel? row, bool skipConfirm)
    {
        if (row is null)
            return;

        var confirmed = false;
        if (!skipConfirm)
        {
            confirmed = await _dialogs.ConfirmAsync(new ConfirmDialogViewModel(
                Localization,
                titleKey: "Chips.Delete.ConfirmTitle",
                messageKey: "Chips.Delete.ConfirmMessage"));
            if (!confirmed)
                return;
        }

        if (_saveTimers.TryGetValue(row.Id, out var cts))
        {
            cts.Cancel();
            _saveTimers.Remove(row.Id);
        }

        // Remove from the grid immediately and run the SQLite delete in the background — the user
        // never waits on the DB for a delete. If the removed row was the focused one, move the
        // selection onto its neighbour so the grid keeps a sensible focus instead of clearing it.
        row.PropertyChanged -= OnRowPropertyChanged;
        if (ReferenceEquals(SelectedChip, row))
            SelectedChip = GridSelection.NeighbourAfterRemoval(Chips, row);
        Chips.Remove(row);

        // Removing one of a duplicate pair may leave the other as the sole holder of that number —
        // re-flag collisions so the freed row loses its red tint without waiting for a reload.
        RefreshDuplicates();

        var id = row.Id;
        _ = Task.Run(() => _editor.DeleteRentalChipAsync(id));

        // The confirmation modal stole keyboard focus to the overlay; pull it back to the grid
        // (now on the new selected row) so focus doesn't end up on the top menu.
        if (confirmed)
            RequestGridFocus();
    }

    // --- Auto-read wiring --------------------------------------------------------------------------

    partial void OnAutoReadEnabledChanged(bool value)
    {
        if (value)
        {
            StartAutoRead();
            ShowActivity();
        }
        else
        {
            _poller.Stop();
            HideActivity();
        }
    }

    // A path or interval change while watching restarts the poll with the new settings. Skip the
    // restart while paused — the poller is intentionally stopped and Resume will pick up the new value.
    partial void OnAutoReadFilePathChanged(string value)
    {
        if (AutoReadEnabled && !IsAutoReadPaused)
            StartAutoRead();
        UpdateActivityStatus();
    }

    partial void OnAutoReadIntervalSecondsChanged(int value)
    {
        if (AutoReadEnabled && !IsAutoReadPaused)
            StartAutoRead();
        UpdateActivityStatus();
    }

    [RelayCommand]
    private void IncrementInterval() => AutoReadIntervalSeconds++;

    [RelayCommand]
    private void DecrementInterval()
    {
        if (AutoReadIntervalSeconds > 1)
            AutoReadIntervalSeconds--;
    }

    private void StartAutoRead()
    {
        if (string.IsNullOrWhiteSpace(AutoReadFilePath))
        {
            _poller.Stop();
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(1, AutoReadIntervalSeconds));
        _poller.Start(AutoReadFilePath, interval, OnPolledContentAsync);
    }

    // Runs on a pool thread (the poller's loop). Parse + import are synchronous SQLite work, so they
    // stay off the UI thread; only the reload hops back. Silent: it just merges new chips.
    private async Task OnPolledContentAsync(string content)
    {
        if (string.IsNullOrWhiteSpace(content) || _session.CurrentEvent is null)
            return;

        try
        {
            var parser = await _readoutParsers.GetCurrentAsync();
            var data = parser.Parse(content);
            var result = await _editor.ImportRentalChipsAsync(data, note: AutoReadNote);
            if (result.Added > 0)
                await Dispatcher.UIThread.InvokeAsync(LoadAsync);
        }
        catch (ReadoutFormatException)
        {
            // Not a readable readout file right now (e.g. half-written); skip this tick.
        }
    }

    // Used on competition switch: turning the toggle off runs OnAutoReadEnabledChanged → Stop, which
    // is idempotent, so a plain assignment both stops the poll and reflects it in the UI.
    private void StopAutoRead() => AutoReadEnabled = false;

    // --- Top-bar background activity ---------------------------------------------------------------

    // Creates the activity handle and registers it so the menu-bar block shows auto-read while it runs.
    private void ShowActivity()
    {
        IsAutoReadPaused = false;
        _autoReadActivity = new ChipAutoReadActivity(
            Localization,
            pause: PauseAutoRead,
            resume: ResumeAutoRead,
            stop: StopAutoRead,
            openSettings: () => NavigateToSelfRequested?.Invoke(this, EventArgs.Empty));
        UpdateActivityStatus();
        _activities.Register(_autoReadActivity);
    }

    // Removes the activity from the block (on Stop or competition switch).
    private void HideActivity()
    {
        IsAutoReadPaused = false;
        if (_autoReadActivity is null)
            return;
        _activities.Unregister(_autoReadActivity);
        _autoReadActivity = null;
    }

    // Pause: stop the poller but keep the toggle on and the path intact, so Resume restarts cleanly.
    private void PauseAutoRead()
    {
        if (!AutoReadEnabled || IsAutoReadPaused)
            return;
        _poller.Stop();
        IsAutoReadPaused = true;
        UpdateActivityStatus();
    }

    private void ResumeAutoRead()
    {
        if (!AutoReadEnabled || !IsAutoReadPaused)
            return;
        IsAutoReadPaused = false;
        StartAutoRead();
        UpdateActivityStatus();
    }

    // Keeps the activity's status line in sync with the watched file / interval / paused state.
    private void UpdateActivityStatus()
    {
        if (_autoReadActivity is null)
            return;

        var fileName = string.IsNullOrWhiteSpace(AutoReadFilePath)
            ? "—"
            : Path.GetFileName(AutoReadFilePath);
        var key = IsAutoReadPaused
            ? "Activity.ChipAutoRead.StatusPaused"
            : "Activity.ChipAutoRead.Status";
        _autoReadActivity.StatusText = string.Format(Localization.Get(key), fileName, AutoReadIntervalSeconds);
    }

    // --- Debounced save ----------------------------------------------------------------------------

    private void RequestRowSave(RentalChipRowViewModel row)
    {
        if (_saveTimers.TryGetValue(row.Id, out var existing))
            existing.Cancel();

        var cts = new CancellationTokenSource();
        _saveTimers[row.Id] = cts;
        _ = SaveRowDebouncedAsync(row, cts.Token);
    }

    private async Task SaveRowDebouncedAsync(RentalChipRowViewModel row, CancellationToken token)
    {
        try
        {
            await Task.Delay(SaveDebounce, token);
            var entity = row.ToEntity();
            await Task.Run(() => _editor.UpdateRentalChipAsync(entity, token), token);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer edit (or the page reloaded) — ignore.
        }
        catch
        {
            // Background save failed; never crash the UI over an autosave.
        }
    }

    private void CancelAllTimers()
    {
        foreach (var cts in _saveTimers.Values)
            cts.Cancel();
        _saveTimers.Clear();
    }
}
