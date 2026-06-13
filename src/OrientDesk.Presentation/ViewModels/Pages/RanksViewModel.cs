using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientDesk.BusinessLogic.Entities;
using OrientDesk.BusinessLogic.Interfaces;
using OrientDesk.Localization;
using OrientDesk.Presentation.Services;
using OrientDesk.Presentation.ViewModels.Dialogs;

namespace OrientDesk.Presentation.ViewModels.Pages;

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
    private readonly Dictionary<Guid, CancellationTokenSource> _saveTimers = new();

    public RanksViewModel(
        ILocalizationService localization,
        IAppStore appStore,
        IBusyService busy,
        IDialogService dialogs)
        : base(localization)
    {
        _appStore = appStore;
        _busy = busy;
        _dialogs = dialogs;
    }

    public override string NavKey => "Nav.Ranks";
    public override string TitleKey => "Page.Ranks.Title";
    public override string TextKey => "Page.Ranks.Text";

    public ObservableCollection<RankRowViewModel> Ranks { get; } = [];

    /// <summary>The row selected in the grid; the Delete key acts on it.</summary>
    [ObservableProperty]
    private RankRowViewModel? _selectedRank;

    /// <summary>Reloads the ranks. Called when the page is shown.</summary>
    public async Task LoadAsync()
    {
        CancelAllTimers();

        var ranks = await _busy.RunAsync(() => _appStore.GetRanksAsync());

        Ranks.Clear();
        foreach (var rank in ranks)
            Ranks.Add(CreateRow(rank));
    }

    private RankRowViewModel CreateRow(SportRank rank) =>
        new(rank, Localization, RequestRowSave);

    [RelayCommand]
    private async Task AddRankAsync()
    {
        // Persist immediately so the new row carries its real id for later debounced updates.
        var rank = await _busy.RunAsync(() => _appStore.AddRankAsync());
        Ranks.Add(CreateRow(rank));
    }

    // The grid's delete button binds to this command. A plain click confirms first; Ctrl+Click and
    // the Delete key route through the no-confirm / selected variants below.
    [RelayCommand]
    private Task DeleteRankAsync(RankRowViewModel? row) => RemoveRankAsync(row, skipConfirm: false);

    /// <summary>Deletes a row without the confirmation prompt (Ctrl+Click / Ctrl+Delete).</summary>
    public Task DeleteRankNoConfirmAsync(RankRowViewModel? row) => RemoveRankAsync(row, skipConfirm: true);

    /// <summary>Deletes the currently selected rank (Delete key); confirms unless skipConfirm.</summary>
    public Task DeleteSelectedRankAsync(bool skipConfirm) => RemoveRankAsync(SelectedRank, skipConfirm);

    private async Task RemoveRankAsync(RankRowViewModel? row, bool skipConfirm)
    {
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

    // --- Debounced save ----------------------------------------------------------------------------

    private void RequestRowSave(RankRowViewModel row)
    {
        if (_saveTimers.TryGetValue(row.Id, out var existing))
            existing.Cancel();

        var cts = new CancellationTokenSource();
        _saveTimers[row.Id] = cts;
        _ = SaveRowDebouncedAsync(row, cts.Token);
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
