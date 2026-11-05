using Avalonia.Threading;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.Localization;
using OrientPyx.Presentation.Services;
using OrientPyx.Presentation.ViewModels.Dialogs;

namespace OrientPyx.Presentation.ViewModels.Pages;

/// <summary>
/// The application-level sports-rank ("Розряди") settings table: a rank name and its points per row.
/// Unlike the region/club grids, ranks live in the app database (shared across every competition), so
/// this page talks to <see cref="IAppStore"/> directly and is independent of the selected competition.
/// Cells auto-save in the background (debounced per row), mirroring <see cref="RegionsViewModel"/>.
/// A participant references a rank only by its name (text), so editing/deleting a rank here never
/// rewrites participant data.
/// </summary>
public sealed partial class RanksViewModel : PageViewModelBase
{
    private static readonly TimeSpan SaveDebounce = TimeSpan.FromMilliseconds(600);

    private readonly IAppStore _appStore;
    private readonly IBusyService _busy;
    private readonly IDialogService _dialogs;
    private readonly IWorkspaceMode _mode;
    private readonly Dictionary<Guid, CancellationTokenSource> _saveTimers = new();
    private readonly PendingSaves _pendingSaves = new();

    public RanksViewModel(
        ILocalizationService localization,
        IAppStore appStore,
        IBusyService busy,
        IDialogService dialogs,
        ITableLayoutStore layoutStore,
        ISessionService session,
        IDataChangeBus bus,
        IWorkspaceMode mode)
        : base(localization)
    {
        LayoutStore = layoutStore;
        _appStore = appStore;
        _busy = busy;
        _dialogs = dialogs;
        _mode = mode;
        // Live data: the host's app-data tables (DataArea.AppData is published with no competition key).
        ObserveData(bus, session, mode, DataArea.AppData);
        mode.Changed += (_, _) => Dispatcher.UIThread.Post(RaiseReadOnly);
        Localization.PropertyChanged += (_, _) => OnPropertyChanged(nameof(ReadOnlyText));
    }

    /// <summary>
    /// True on a station: the ranks are edited on the host — the results use the host's tables (through
    /// IResultsAppData), so this page only shows this machine's own copy. The tables are locked, the add/delete
    /// actions are off and <see cref="ReadOnlyText"/> says so.
    /// </summary>
    public bool IsReadOnly => _mode.IsStation;

    /// <summary>The banner text shown while <see cref="IsReadOnly"/>.</summary>
    public string ReadOnlyText => Localization.Get("Banner.StationReadOnly");

    private void RaiseReadOnly()
    {
        OnPropertyChanged(nameof(IsReadOnly));
        OnPropertyChanged(nameof(ReadOnlyText));
    }

    /// <summary>Per-competition table-view store; persists this page's tables' column order/width/visibility.</summary>
    public ITableLayoutStore LayoutStore { get; }

    public override string NavKey => "Nav.Ranks";
    public override string TitleKey => "Page.Ranks.Title";
    public override string TextKey => "Page.Ranks.Text";

    public ObservableCollection<RankRowViewModel> Ranks { get; } = [];

    /// <summary>The rank qualification table (Додаток 89), one row per course-rank threshold.</summary>
    public ObservableCollection<RankQualRowViewModel> Qual { get; } = [];

    /// <summary>The row selected in the grid; the Delete key acts on it.</summary>
    [ObservableProperty]
    private RankRowViewModel? _selectedRank;

    /// <summary>The selected row in the qualification table; its Delete key acts on it.</summary>
    [ObservableProperty]
    private RankQualRowViewModel? _selectedQual;

    /// <summary>Minimum participants in a group for any rank to be valid (editable text). Debounced save.</summary>
    [ObservableProperty]
    private string _minParticipantsText = "3";

    /// <summary>Minimum distinct regions across the competition for any rank to be valid (editable text).</summary>
    [ObservableProperty]
    private string _minRegionsText = "8";

    /// <summary>How many top-ranked participants count toward a group's course rank (editable text). Default 12.</summary>
    [ObservableProperty]
    private string _countForRankText = "12";

    // Guards the conditions debounce against the initial load assignment.
    private bool _conditionsLoaded;
    private CancellationTokenSource? _conditionsTimer;

    /// <inheritdoc />
    /// <remarks>
    /// The tables live in this machine's app database. Locally that is the only copy; on a station the page shows
    /// the station's own tables while the results are computed from the host's (through IResultsAppData), hence
    /// read-only there (see <see cref="IsReadOnly"/>). A reload follows DataArea.AppData, published by the host.
    /// </remarks>
    protected override async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        // The two tables are merged in place (RowSync), so the selections survive and only changed cells repaint.
        await _pendingSaves.WaitAsync();
        await LoadCoreAsync(merge: true);
        if (SelectedRank is { } rank && !Ranks.Contains(rank))
            SelectedRank = null;
        if (SelectedQual is { } qual && !Qual.Contains(qual))
            SelectedQual = null;
    }

    /// <summary>Reloads the ranks. Called when the page is shown.</summary>
    public async Task LoadAsync()
    {
        CancelAllTimers();
        await LoadCoreAsync(merge: false);
    }

    // Reads the tables and either rebuilds them (first open) or merges them in place (a live refresh).
    private async Task LoadCoreAsync(bool merge)
    {
        _conditionsLoaded = false;

        var ranks = await _busy.RunAsync(() => _appStore.GetRanksAsync());
        var qual = await _busy.RunAsync(() => _appStore.GetRankQualificationAsync());
        var conditions = await _busy.RunAsync(() => _appStore.GetRankConditionsAsync()) ?? (3, 8, 12);

        if (!merge)
        {
            Ranks.Clear();
            Qual.Clear();
        }

        RowSync.Sync(Ranks, ranks,
            itemKey: rank => rank.Id,
            rowKey: row => row.Id,
            create: CreateRow,
            update: (row, rank) => row.Apply(rank));

        RowSync.Sync(Qual, qual,
            itemKey: item => item.Id,
            rowKey: row => row.Id,
            create: CreateQualRow,
            update: (row, item) => row.Apply(item));

        MinParticipantsText = conditions.Item1.ToString(System.Globalization.CultureInfo.InvariantCulture);
        MinRegionsText = conditions.Item2.ToString(System.Globalization.CultureInfo.InvariantCulture);
        CountForRankText = conditions.Item3.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _conditionsLoaded = true;
    }

    private RankRowViewModel CreateRow(SportRank rank) =>
        new(rank, Localization, RequestRowSave);

    private RankQualRowViewModel CreateQualRow(RankQualificationRow row) =>
        new(row, Localization, RequestQualRowSave);

    [RelayCommand]
    private async Task AddRankAsync()
    {
        if (IsReadOnly)
            return;
        // Persist immediately so the new row carries its real id for later debounced updates.
        var rank = await _busy.RunAsync(() => _appStore.AddRankAsync());
        Ranks.Add(CreateRow(rank));
    }

    [RelayCommand]
    private async Task AddQualRowAsync()
    {
        if (IsReadOnly)
            return;
        var row = await _busy.RunAsync(() => _appStore.AddRankQualificationRowAsync());
        Qual.Add(CreateQualRow(row));
    }

    // The grid's delete button binds to this command. A plain click confirms first; Ctrl+Click and
    // the Delete key route through the no-confirm / selected variants below.
    [RelayCommand]
    private Task DeleteRankAsync(RankRowViewModel? row) => RemoveRankAsync(row, skipConfirm: false);

    [RelayCommand]
    private Task DeleteQualRowAsync(RankQualRowViewModel? row) => RemoveQualRowAsync(row, skipConfirm: false);

    /// <summary>Deletes a qualification row without the confirmation prompt (Ctrl+Click / Ctrl+Delete).</summary>
    public Task DeleteQualRowNoConfirmAsync(RankQualRowViewModel? row) => RemoveQualRowAsync(row, skipConfirm: true);

    /// <summary>Deletes the selected qualification row (Delete key); confirms unless skipConfirm.</summary>
    public Task DeleteSelectedQualRowAsync(bool skipConfirm) => RemoveQualRowAsync(SelectedQual, skipConfirm);

    public Task DeleteRankNoConfirmAsync(RankRowViewModel? row) => RemoveRankAsync(row, skipConfirm: true);

    /// <summary>Deletes the currently selected rank (Delete key); confirms unless skipConfirm.</summary>
    public Task DeleteSelectedRankAsync(bool skipConfirm) => RemoveRankAsync(SelectedRank, skipConfirm);

    private async Task RemoveRankAsync(RankRowViewModel? row, bool skipConfirm)
    {
        if (IsReadOnly)
            return;
        if (row is null)
            return;

        var confirmed = false;
        if (!skipConfirm)
        {
            confirmed = await _dialogs.ConfirmAsync(new ConfirmDialogViewModel(
                Localization,
                titleKey: "Ranks.Delete.ConfirmTitle",
                messageKey: "Ranks.Delete.ConfirmMessage"));
            if (!confirmed)
                return;
        }

        if (_saveTimers.TryGetValue(row.Id, out var cts))
        {
            cts.Cancel();
            _saveTimers.Remove(row.Id);
        }

        // Remove from the grid immediately and run the SQLite delete in the background.
        if (ReferenceEquals(SelectedRank, row))
            SelectedRank = GridSelection.NeighbourAfterRemoval(Ranks, row);
        Ranks.Remove(row);

        var id = row.Id;
        _ = Task.Run(() => _appStore.DeleteRankAsync(id));

        if (confirmed)
            RequestGridFocus();
    }

    private async Task RemoveQualRowAsync(RankQualRowViewModel? row, bool skipConfirm)
    {
        if (IsReadOnly)
            return;
        if (row is null)
            return;

        if (!skipConfirm)
        {
            var confirmed = await _dialogs.ConfirmAsync(new ConfirmDialogViewModel(
                Localization,
                titleKey: "Ranks.Qual.Delete.ConfirmTitle",
                messageKey: "Ranks.Qual.Delete.ConfirmMessage"));
            if (!confirmed)
                return;
        }

        if (_saveTimers.TryGetValue(row.Id, out var cts))
        {
            cts.Cancel();
            _saveTimers.Remove(row.Id);
        }

        if (ReferenceEquals(SelectedQual, row))
            SelectedQual = GridSelection.NeighbourAfterRemoval(Qual, row);
        Qual.Remove(row);

        var id = row.Id;
        _ = Task.Run(() => _appStore.DeleteRankQualificationRowAsync(id));
    }

    // --- Conditions (debounced)

    partial void OnMinParticipantsTextChanged(string value) => QueueConditionsSave();
    partial void OnMinRegionsTextChanged(string value) => QueueConditionsSave();
    partial void OnCountForRankTextChanged(string value) => QueueConditionsSave();

    private void QueueConditionsSave()
    {
        if (!_conditionsLoaded || IsReadOnly)
            return;
        _conditionsTimer?.Cancel();
        var cts = new CancellationTokenSource();
        _conditionsTimer = cts;
        _pendingSaves.Track(SaveConditionsDebouncedAsync(cts.Token));
    }

    private async Task SaveConditionsDebouncedAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(SaveDebounce, token);
            var min = ParsePositive(MinParticipantsText);
            var regions = ParsePositive(MinRegionsText);
            var count = ParsePositive(CountForRankText);
            await Task.Run(() => _appStore.SaveRankConditionsAsync(min, regions, count, token), token);
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    // A condition floor is at least 1 (0 / blank / garbage ⇒ "no restriction").
    private static int ParsePositive(string? text)
    {
        if (int.TryParse((text ?? string.Empty).Trim(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var value) && value > 0)
            return value;
        return 1;
    }

    // --- Debounced save

    private void RequestRowSave(RankRowViewModel row)
    {
        if (IsReadOnly || row.IsApplying)
            return;
        if (_saveTimers.TryGetValue(row.Id, out var existing))
            existing.Cancel();

        var cts = new CancellationTokenSource();
        _saveTimers[row.Id] = cts;
        _pendingSaves.Track(SaveRowDebouncedAsync(row, cts.Token));
    }

    private async Task SaveRowDebouncedAsync(RankRowViewModel row, CancellationToken token)
    {
        try
        {
            await Task.Delay(SaveDebounce, token);
            var entity = row.ToEntity();
            await Task.Run(() => _appStore.UpdateRankAsync(entity, token), token);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
    }

    private void RequestQualRowSave(RankQualRowViewModel row)
    {
        if (IsReadOnly || row.IsApplying)
            return;
        if (_saveTimers.TryGetValue(row.Id, out var existing))
            existing.Cancel();

        var cts = new CancellationTokenSource();
        _saveTimers[row.Id] = cts;
        _pendingSaves.Track(SaveQualRowDebouncedAsync(row, cts.Token));
    }

    private async Task SaveQualRowDebouncedAsync(RankQualRowViewModel row, CancellationToken token)
    {
        try
        {
            await Task.Delay(SaveDebounce, token);
            var entity = row.ToEntity();
            await Task.Run(() => _appStore.UpdateRankQualificationRowAsync(entity, token), token);
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private void CancelAllTimers()
    {
        foreach (var cts in _saveTimers.Values)
            cts.Cancel();
        _saveTimers.Clear();
        _conditionsTimer?.Cancel();
    }
}
