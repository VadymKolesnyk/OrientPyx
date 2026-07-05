using System.Collections.ObjectModel;
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
/// Spreadsheet-like sports-school (ДЮСШ) database for the CURRENT competition (schools are
/// competition-level, so there is no day picker). Each row is a school name plus a read-only count
/// of participants attending it. Cells auto-save in the background (debounced per row) like the
/// other grid pages. Mirrors <see cref="RegionsViewModel"/>.
/// </summary>
public sealed partial class DusshViewModel : PageViewModelBase
{
    private static readonly TimeSpan SaveDebounce = TimeSpan.FromMilliseconds(600);

    private readonly ICompetitionEditorService _editor;
    private readonly ISessionService _session;
    private readonly IBusyService _busy;
    private readonly IDialogService _dialogs;
    private readonly Dictionary<Guid, CancellationTokenSource> _saveTimers = new();

    public DusshViewModel(
        ILocalizationService localization,
        ICompetitionEditorService editor,
        ISessionService session,
        IBusyService busy,
        IDialogService dialogs,
        ITableLayoutStore layoutStore)
        : base(localization)
    {
        LayoutStore = layoutStore;
        _editor = editor;
        _session = session;
        _busy = busy;
        _dialogs = dialogs;

        // Singleton VM: reload when the competition changes (the event can be raised on a pool thread).
        _session.SessionChanged += (_, _) => Dispatcher.UIThread.Post(() => _ = LoadAsync());
    }

    /// <summary>Per-competition table-view store; persists this page's table column order/width/visibility.</summary>
    public ITableLayoutStore LayoutStore { get; }

    public override string NavKey => "Nav.Dussh";
    public override string TitleKey => "Page.Dussh.Title";
    public override string TextKey => "Page.Dussh.Text";

    public ObservableCollection<DusshRowViewModel> Dusshes { get; } = [];

    /// <summary>The row selected in the grid; the Delete key acts on it.</summary>
    [ObservableProperty]
    private DusshRowViewModel? _selectedDussh;

    /// <summary>Reloads the schools for the current competition. Called when the page is shown.</summary>
    public async Task LoadAsync()
    {
        CancelAllTimers();

        var hasEvent = _session.CurrentEvent is not null;
        var dusshes = hasEvent
            ? await _busy.RunAsync(() => _editor.GetDusshesAsync())
            : (IReadOnlyList<Dussh>)[];
        var counts = hasEvent
            ? await _busy.RunAsync(() => _editor.GetDusshParticipantCountsAsync())
            : (IReadOnlyDictionary<Guid, int>)new Dictionary<Guid, int>();

        Dusshes.Clear();
        foreach (var dussh in dusshes)
        {
            var row = CreateRow(dussh);
            row.ParticipantCount = counts.TryGetValue(dussh.Id, out var c) ? c : 0;
            Dusshes.Add(row);
        }
    }

    private DusshRowViewModel CreateRow(Dussh dussh) =>
        new(dussh, Localization, RequestRowSave);

    [RelayCommand]
    private async Task AddDusshAsync()
    {
        if (_session.CurrentEvent is null)
            return;

        // Persist immediately so the new row carries its real id for later debounced updates.
        var dussh = await _busy.RunAsync(() => _editor.AddDusshRowAsync());
        Dusshes.Add(CreateRow(dussh));
    }

    // The grid's delete button binds to this command. A plain click confirms first; Ctrl+Click and
    // the Delete key route through the no-confirm / selected variants below.
    [RelayCommand]
    private Task DeleteDusshAsync(DusshRowViewModel? row) => RemoveDusshAsync(row, skipConfirm: false);

    /// <summary>Deletes a row without the confirmation prompt (Ctrl+Click / Ctrl+Delete).</summary>
    public Task DeleteDusshNoConfirmAsync(DusshRowViewModel? row) => RemoveDusshAsync(row, skipConfirm: true);

    /// <summary>Deletes the currently selected school (Delete key); confirms unless skipConfirm.</summary>
    public Task DeleteSelectedDusshAsync(bool skipConfirm) => RemoveDusshAsync(SelectedDussh, skipConfirm);

    private async Task RemoveDusshAsync(DusshRowViewModel? row, bool skipConfirm)
    {
        if (row is null)
            return;

        var confirmed = false;
        if (!skipConfirm)
        {
            confirmed = await _dialogs.ConfirmAsync(new ConfirmDialogViewModel(
                Localization,
                titleKey: "Dussh.Delete.ConfirmTitle",
                messageKey: "Dussh.Delete.ConfirmMessage"));
            if (!confirmed)
                return;
        }

        if (_saveTimers.TryGetValue(row.Id, out var cts))
        {
            cts.Cancel();
            _saveTimers.Remove(row.Id);
        }

        // Remove from the grid immediately and run the SQLite delete in the background — the user never
        // waits on the DB for a delete. The service clears this school from any participant using it.
        if (ReferenceEquals(SelectedDussh, row))
            SelectedDussh = GridSelection.NeighbourAfterRemoval(Dusshes, row);
        Dusshes.Remove(row);

        var id = row.Id;
        _ = Task.Run(() => _editor.DeleteDusshAsync(id));

        if (confirmed)
            RequestGridFocus();
    }

    // --- Debounced save ----------------------------------------------------------------------------

    private void RequestRowSave(DusshRowViewModel row)
    {
        if (_saveTimers.TryGetValue(row.Id, out var existing))
            existing.Cancel();

        var cts = new CancellationTokenSource();
        _saveTimers[row.Id] = cts;
        _ = SaveRowDebouncedAsync(row, cts.Token);
    }

    private async Task SaveRowDebouncedAsync(DusshRowViewModel row, CancellationToken token)
    {
        try
        {
            await Task.Delay(SaveDebounce, token);
            var entity = row.ToEntity();
            await Task.Run(() => _editor.UpdateDusshAsync(entity, token), token);
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
