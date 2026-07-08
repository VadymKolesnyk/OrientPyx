using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Localization;
using OrientPyx.Presentation.Services;

namespace OrientPyx.Presentation.ViewModels;

/// <summary>
/// Competition picker shown until a selection is made. Lists competitions discovered by
/// scanning the events folder and lets the user open one or create a new one. Competitions can be
/// hidden from the list; a "show hidden" switch reveals them (and lets the user unhide them).
/// </summary>
public sealed partial class EventSelectionViewModel : ViewModelBase
{
    private readonly IEventCatalogService _catalog;
    private readonly IEventStore _eventStore;
    private readonly IAppStore _appStore;
    private readonly ISessionService _session;
    private readonly IBusyService _busy;

    // Every scanned competition, hidden or not. The visible Events collection is a filtered view.
    private readonly List<EventSummaryRowViewModel> _all = [];

    [ObservableProperty]
    private EventSummaryRowViewModel? _selectedEvent;

    [ObservableProperty]
    private bool _showHidden;

    public EventSelectionViewModel(
        IEventCatalogService catalog,
        IEventStore eventStore,
        IAppStore appStore,
        ISessionService session,
        IBusyService busy,
        ILocalizationService localization)
    {
        _catalog = catalog;
        _eventStore = eventStore;
        _appStore = appStore;
        _session = session;
        _busy = busy;
        Localization = localization;
    }

    public ILocalizationService Localization { get; }

    /// <summary>The rows currently shown in the table (hidden ones only when <see cref="ShowHidden"/> is on).</summary>
    public ObservableCollection<EventSummaryRowViewModel> Events { get; } = [];

    /// <summary>True when at least one competition is hidden — used to show the "show hidden" switch.</summary>
    public bool HasHidden => _all.Exists(r => r.IsHidden);

    /// <summary>Raised when the user wants to create a new competition.</summary>
    public event EventHandler? CreateRequested;

    /// <summary>Reads the competition list (off the UI thread when called inside a busy scope).</summary>
    public Task<IReadOnlyList<EventSummary>> FetchEventsAsync() => _catalog.GetEventsAsync();

    /// <summary>Replaces the displayed list. Must run on the UI thread (touches the collection).</summary>
    public void Populate(IReadOnlyList<EventSummary> items)
    {
        _all.Clear();
        foreach (var item in items)
            _all.Add(new EventSummaryRowViewModel(item));

        ApplyFilter();
    }

    // Rebuilds the visible Events collection from _all, honouring the ShowHidden switch.
    private void ApplyFilter()
    {
        var selected = SelectedEvent;

        Events.Clear();
        foreach (var row in _all)
        {
            if (row.IsHidden && !ShowHidden)
                continue;
            Events.Add(row);
        }

        // Keep the selection if it is still visible; otherwise drop it.
        SelectedEvent = selected is not null && Events.Contains(selected) ? selected : null;

        OnPropertyChanged(nameof(HasHidden));
    }

    partial void OnShowHiddenChanged(bool value) => ApplyFilter();

    private bool CanOpen() => SelectedEvent is not null;

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private Task OpenAsync() => _busy.RunAsync(async () =>
    {
        if (SelectedEvent is null)
            return;

        var summary = SelectedEvent.Summary;
        var days = await _eventStore.GetDaysAsync(summary.FolderPath);
        if (days.Count == 0)
            return;

        // Land on the remembered day for this competition (the app DB keeps a single last
        // session), otherwise the first day. The per-page dropdown can switch days afterwards.
        var (lastId, lastDay) = await _appStore.GetLastSessionAsync();
        var day = (lastId == summary.Identifier
                      ? days.FirstOrDefault(d => d.Number == lastDay)
                      : null)
                  ?? days[0];

        await _session.SelectAsync(summary, day);
    });

    /// <summary>Toggles the hidden flag on a row (used by the per-row action button).</summary>
    [RelayCommand]
    private async Task ToggleHiddenAsync(EventSummaryRowViewModel row)
    {
        var hidden = !row.IsHidden;

        // Only the DB write is offloaded to the background thread by BusyService; the UI-collection
        // updates below must run on the UI thread (they resume here after the awaited call returns).
        await _busy.RunAsync(() => _catalog.SetHiddenAsync(row.Summary, hidden));

        row.RaiseHiddenChanged();
        ApplyFilter();
    }

    [RelayCommand]
    private void Create() => CreateRequested?.Invoke(this, EventArgs.Empty);

    partial void OnSelectedEventChanged(EventSummaryRowViewModel? value) => OpenCommand.NotifyCanExecuteChanged();
}
