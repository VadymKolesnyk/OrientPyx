using System.Collections.ObjectModel;
using Avalonia.Threading;
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
    private readonly IWorkspaceMode _mode;

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
        ILocalizationService localization,
        ITableLayoutStore layoutStore,
        IWorkspaceMode mode)
    {
        _catalog = catalog;
        _eventStore = eventStore;
        _appStore = appStore;
        _session = session;
        _busy = busy;
        _mode = mode;
        Localization = localization;
        LayoutStore = layoutStore;
        // The role switches on a transport thread; the «Станція · host» line follows it.
        _mode.Changed += (_, _) => Dispatcher.UIThread.Post(RaiseStation);
        Localization.PropertyChanged += (_, _) => RaiseStation();
    }

    /// <summary>True while the listed competitions are a host's (this instance is a station).</summary>
    public bool IsStation => _mode.IsStation;

    /// <summary>«Станція · Головний-ноут» under the title while connected to a host.</summary>
    public string StationLine => _mode.IsStation
        ? string.Format(Localization.Get("Network.Role.Station"), _mode.Station?.HostName ?? string.Empty)
        : string.Empty;

    private void RaiseStation()
    {
        OnPropertyChanged(nameof(IsStation));
        OnPropertyChanged(nameof(StationLine));
    }

    /// <summary>Raised by «Підключитися до хоста…» — the root coordinator opens the «Спільна робота» overlay.</summary>
    public event EventHandler? ConnectRequested;

    [RelayCommand]
    private void ConnectToHost() => ConnectRequested?.Invoke(this, EventArgs.Empty);

    public ILocalizationService Localization { get; }

    /// <summary>Persists the table's column view. No competition is selected on this screen, so only the
    /// application-default layer applies here — the arrangement the user saves for the next competitions.</summary>
    public ITableLayoutStore LayoutStore { get; }

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

    /// <summary>
    /// Opens a competition by identifier (and a day number, else the first day) without a row being picked — the
    /// station lands in whatever the host has open. False when the list does not hold it.
    /// </summary>
    public async Task<bool> TryOpenAsync(string identifier, int? dayNumber)
    {
        var row = _all.FirstOrDefault(r => string.Equals(r.Identifier, identifier, StringComparison.OrdinalIgnoreCase));
        if (row is null)
            return false;
        return await _busy.RunAsync(async () =>
        {
            var days = await _eventStore.GetDaysAsync(row.Summary.FolderPath);
            if (days.Count == 0)
                return false;
            var day = days.FirstOrDefault(d => d.Number == dayNumber) ?? days[0];
            await _session.SelectAsync(row.Summary, day);
            return true;
        });
    }

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
