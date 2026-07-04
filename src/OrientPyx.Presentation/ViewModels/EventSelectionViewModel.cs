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
/// scanning the events folder and lets the user open one or create a new one.
/// </summary>
public sealed partial class EventSelectionViewModel : ViewModelBase
{
    private readonly IEventCatalogService _catalog;
    private readonly IEventStore _eventStore;
    private readonly IAppStore _appStore;
    private readonly ISessionService _session;
    private readonly IBusyService _busy;

    [ObservableProperty]
    private EventSummary? _selectedEvent;

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

    public ObservableCollection<EventSummary> Events { get; } = [];

    /// <summary>Raised when the user wants to create a new competition.</summary>
    public event EventHandler? CreateRequested;

    /// <summary>Reads the competition list (off the UI thread when called inside a busy scope).</summary>
    public Task<IReadOnlyList<EventSummary>> FetchEventsAsync() => _catalog.GetEventsAsync();

    /// <summary>Replaces the displayed list. Must run on the UI thread (touches the collection).</summary>
    public void Populate(IReadOnlyList<EventSummary> items)
    {
        Events.Clear();
        foreach (var item in items)
            Events.Add(item);
    }

    private bool CanOpen() => SelectedEvent is not null;

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private Task OpenAsync() => _busy.RunAsync(async () =>
    {
        if (SelectedEvent is null)
            return;

        var days = await _eventStore.GetDaysAsync(SelectedEvent.FolderPath);
        if (days.Count == 0)
            return;

        // Land on the remembered day for this competition (the app DB keeps a single last
        // session), otherwise the first day. The per-page dropdown can switch days afterwards.
        var (lastId, lastDay) = await _appStore.GetLastSessionAsync();
        var day = (lastId == SelectedEvent.Identifier
                      ? days.FirstOrDefault(d => d.Number == lastDay)
                      : null)
                  ?? days[0];

        await _session.SelectAsync(SelectedEvent, day);
    });

    [RelayCommand]
    private void Create() => CreateRequested?.Invoke(this, EventArgs.Empty);

    partial void OnSelectedEventChanged(EventSummary? value) => OpenCommand.NotifyCanExecuteChanged();
}
