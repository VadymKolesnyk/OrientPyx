using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientDesk.BusinessLogic.Entities;
using OrientDesk.BusinessLogic.Interfaces;
using OrientDesk.Localization;
using OrientDesk.Presentation.Services;
using OrientDesk.Presentation.ViewModels.Dialogs;

namespace OrientDesk.Presentation.ViewModels.Pages;

/// <summary>
/// Spreadsheet-like region database for the CURRENT competition (regions are competition-level, so
/// there is no day picker). Each row is a region name plus a read-only count of participants from it.
/// Cells auto-save in the background (debounced per row) like the other grid pages. Mirrors
/// <see cref="ChipsViewModel"/> minus the auto-read / bulk / clear machinery.
/// </summary>
public sealed partial class RegionsViewModel : PageViewModelBase
{
    private static readonly TimeSpan SaveDebounce = TimeSpan.FromMilliseconds(600);

    private readonly ICompetitionEditorService _editor;
    private readonly ISessionService _session;
    private readonly IBusyService _busy;
    private readonly IDialogService _dialogs;
    private readonly Dictionary<Guid, CancellationTokenSource> _saveTimers = new();

    public RegionsViewModel(
        ILocalizationService localization,
        ICompetitionEditorService editor,
        ISessionService session,
        IBusyService busy,
        IDialogService dialogs)
        : base(localization)
    {
        _editor = editor;
        _session = session;
        _busy = busy;
        _dialogs = dialogs;

        // Singleton VM: reload when the competition changes (the event can be raised on a pool thread).
        _session.SessionChanged += (_, _) => Dispatcher.UIThread.Post(() => _ = LoadAsync());
    }

    public override string NavKey => "Nav.Regions";
    public override string TitleKey => "Page.Regions.Title";
    public override string TextKey => "Page.Regions.Text";

    public ObservableCollection<RegionRowViewModel> Regions { get; } = [];

    /// <summary>The row selected in the grid; the Delete key acts on it.</summary>
    [ObservableProperty]
    private RegionRowViewModel? _selectedRegion;

    /// <summary>Reloads the regions for the current competition. Called when the page is shown.</summary>
    public async Task LoadAsync()
    {
        CancelAllTimers();

        var hasEvent = _session.CurrentEvent is not null;
        var regions = hasEvent
            ? await _busy.RunAsync(() => _editor.GetRegionsAsync())
            : (IReadOnlyList<Region>)[];
        var counts = hasEvent
            ? await _busy.RunAsync(() => _editor.GetRegionParticipantCountsAsync())
            : (IReadOnlyDictionary<Guid, int>)new Dictionary<Guid, int>();

        Regions.Clear();
        foreach (var region in regions)
        {
            var row = CreateRow(region);
            row.ParticipantCount = counts.TryGetValue(region.Id, out var c) ? c : 0;
            Regions.Add(row);
        }
    }

    private RegionRowViewModel CreateRow(Region region) =>
        new(region, Localization, RequestRowSave);

    [RelayCommand]
    private async Task AddRegionAsync()
    {
        if (_session.CurrentEvent is null)
            return;

        // Persist immediately so the new row carries its real id for later debounced updates.
        var region = await _busy.RunAsync(() => _editor.AddRegionRowAsync());
        Regions.Add(CreateRow(region));
    }

    // The grid's delete button binds to this command. A plain click confirms first; Ctrl+Click and
    // the Delete key route through the no-confirm / selected variants below.
    [RelayCommand]
    private Task DeleteRegionAsync(RegionRowViewModel? row) => RemoveRegionAsync(row, skipConfirm: false);

    /// <summary>Deletes a row without the confirmation prompt (Ctrl+Click / Ctrl+Delete).</summary>
    public Task DeleteRegionNoConfirmAsync(RegionRowViewModel? row) => RemoveRegionAsync(row, skipConfirm: true);

    /// <summary>Deletes the currently selected region (Delete key); confirms unless skipConfirm.</summary>
    public Task DeleteSelectedRegionAsync(bool skipConfirm) => RemoveRegionAsync(SelectedRegion, skipConfirm);

    private async Task RemoveRegionAsync(RegionRowViewModel? row, bool skipConfirm)
    {
        if (row is null)
            return;

        var confirmed = false;
        if (!skipConfirm)
        {
            confirmed = await _dialogs.ConfirmAsync(new ConfirmDialogViewModel(
                Localization,
                titleKey: "Regions.Delete.ConfirmTitle",
                messageKey: "Regions.Delete.ConfirmMessage"));
            if (!confirmed)
                return;
        }

        if (_saveTimers.TryGetValue(row.Id, out var cts))
        {
            cts.Cancel();
            _saveTimers.Remove(row.Id);
        }

        // Remove from the grid immediately and run the SQLite delete in the background — the user never
        // waits on the DB for a delete. The service clears this region from any participant using it.
        if (ReferenceEquals(SelectedRegion, row))
            SelectedRegion = GridSelection.NeighbourAfterRemoval(Regions, row);
        Regions.Remove(row);

        var id = row.Id;
        _ = Task.Run(() => _editor.DeleteRegionAsync(id));

        if (confirmed)
            RequestGridFocus();
    }

    // --- Debounced save ----------------------------------------------------------------------------

    private void RequestRowSave(RegionRowViewModel row)
    {
        if (_saveTimers.TryGetValue(row.Id, out var existing))
            existing.Cancel();

        var cts = new CancellationTokenSource();
        _saveTimers[row.Id] = cts;
        _ = SaveRowDebouncedAsync(row, cts.Token);
    }

    private async Task SaveRowDebouncedAsync(RegionRowViewModel row, CancellationToken token)
    {
        try
        {
            await Task.Delay(SaveDebounce, token);
            var entity = row.ToEntity();
            await Task.Run(() => _editor.UpdateRegionAsync(entity, token), token);
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
