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
/// Spreadsheet-like club database for the CURRENT competition (clubs are competition-level, so there
/// is no day picker). Each row is a club name plus a read-only count of participants in it. Mirrors
/// <see cref="RegionsViewModel"/>.
/// </summary>
public sealed partial class ClubsViewModel : PageViewModelBase
{
    private static readonly TimeSpan SaveDebounce = TimeSpan.FromMilliseconds(600);

    private readonly ICompetitionEditorService _editor;
    private readonly ISessionService _session;
    private readonly IBusyService _busy;
    private readonly IDialogService _dialogs;
    private readonly Dictionary<Guid, CancellationTokenSource> _saveTimers = new();

    public ClubsViewModel(
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

        _session.SessionChanged += (_, _) => Dispatcher.UIThread.Post(() => _ = LoadAsync());
    }

    /// <summary>Per-competition table-view store; persists this page's table column order/width/visibility.</summary>
    public ITableLayoutStore LayoutStore { get; }

    public override string NavKey => "Nav.Clubs";
    public override string TitleKey => "Page.Clubs.Title";
    public override string TextKey => "Page.Clubs.Text";

    public ObservableCollection<ClubRowViewModel> Clubs { get; } = [];

    /// <summary>The row selected in the grid; the Delete key acts on it.</summary>
    [ObservableProperty]
    private ClubRowViewModel? _selectedClub;

    /// <summary>Reloads the clubs for the current competition. Called when the page is shown.</summary>
    public async Task LoadAsync()
    {
        CancelAllTimers();

        var hasEvent = _session.CurrentEvent is not null;
        var clubs = hasEvent
            ? await _busy.RunAsync(() => _editor.GetClubsAsync())
            : (IReadOnlyList<Club>)[];
        var counts = hasEvent
            ? await _busy.RunAsync(() => _editor.GetClubParticipantCountsAsync())
            : (IReadOnlyDictionary<Guid, int>)new Dictionary<Guid, int>();

        Clubs.Clear();
        foreach (var club in clubs)
        {
            var row = CreateRow(club);
            row.ParticipantCount = counts.TryGetValue(club.Id, out var c) ? c : 0;
            Clubs.Add(row);
        }
    }

    private ClubRowViewModel CreateRow(Club club) => new(club, Localization, RequestRowSave);

    [RelayCommand]
    private async Task AddClubAsync()
    {
        if (_session.CurrentEvent is null)
            return;

        var club = await _busy.RunAsync(() => _editor.AddClubRowAsync());
        Clubs.Add(CreateRow(club));
    }

    [RelayCommand]
    private Task DeleteClubAsync(ClubRowViewModel? row) => RemoveClubAsync(row, skipConfirm: false);

    /// <summary>Deletes a row without the confirmation prompt (Ctrl+Click / Ctrl+Delete).</summary>
    public Task DeleteClubNoConfirmAsync(ClubRowViewModel? row) => RemoveClubAsync(row, skipConfirm: true);

    /// <summary>Deletes the currently selected club (Delete key); confirms unless skipConfirm.</summary>
    public Task DeleteSelectedClubAsync(bool skipConfirm) => RemoveClubAsync(SelectedClub, skipConfirm);

    private async Task RemoveClubAsync(ClubRowViewModel? row, bool skipConfirm)
    {
        if (row is null)
            return;

        var confirmed = false;
        if (!skipConfirm)
        {
            confirmed = await _dialogs.ConfirmAsync(new ConfirmDialogViewModel(
                Localization,
                titleKey: "Clubs.Delete.ConfirmTitle",
                messageKey: "Clubs.Delete.ConfirmMessage"));
            if (!confirmed)
                return;
        }

        if (_saveTimers.TryGetValue(row.Id, out var cts))
        {
            cts.Cancel();
            _saveTimers.Remove(row.Id);
        }

        if (ReferenceEquals(SelectedClub, row))
            SelectedClub = GridSelection.NeighbourAfterRemoval(Clubs, row);
        Clubs.Remove(row);

        var id = row.Id;
        _ = Task.Run(() => _editor.DeleteClubAsync(id));

        if (confirmed)
            RequestGridFocus();
    }

    // --- Debounced save ----------------------------------------------------------------------------

    private void RequestRowSave(ClubRowViewModel row)
    {
        if (_saveTimers.TryGetValue(row.Id, out var existing))
            existing.Cancel();

        var cts = new CancellationTokenSource();
        _saveTimers[row.Id] = cts;
        _ = SaveRowDebouncedAsync(row, cts.Token);
    }

    private async Task SaveRowDebouncedAsync(ClubRowViewModel row, CancellationToken token)
    {
        try
        {
            await Task.Delay(SaveDebounce, token);
            var entity = row.ToEntity();
            await Task.Run(() => _editor.UpdateClubAsync(entity, token), token);
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private void CancelAllTimers()
    {
        foreach (var cts in _saveTimers.Values)
            cts.Cancel();
        _saveTimers.Clear();
    }
}
