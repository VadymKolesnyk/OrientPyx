using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientDesk.BusinessLogic.Interfaces;
using OrientDesk.BusinessLogic.Models;
using OrientDesk.Localization;
using OrientDesk.Presentation.Services;

namespace OrientDesk.Presentation.ViewModels;

/// <summary>
/// Competition picker shown until a selection is made. Lists competitions discovered by
/// scanning the events folder and lets the user open one or create a new one.
/// </summary>
public sealed partial class EventSelectionViewModel : ViewModelBase
{
    private readonly IEventCatalogService _catalog;
    private readonly IEventStore _eventStore;
    private readonly ISessionService _session;
    private readonly IBusyService _busy;

    [ObservableProperty]
    private EventSummary? _selectedEvent;

    public EventSelectionViewModel(
        IEventCatalogService catalog,
        IEventStore eventStore,
        ISessionService session,
        IBusyService busy,
        ILocalizationService localization)
    {
        _catalog = catalog;
        _eventStore = eventStore;
        _session = session;
        _busy = busy;
        Localization = localization;
    }

    public ILocalizationService Localization { get; }

    public ObservableCollection<EventSummary> Events { get; } = [];

    /// <summary>Raised when the user wants to create a new competition.</summary>
    public event EventHandler? CreateRequested;

    public async Task LoadAsync()
    {
        Events.Clear();
        var items = await _catalog.GetEventsAsync();
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

        // Open the first day; per-day picking can be added later.
        await _session.SelectAsync(SelectedEvent, days[0]);
    });

    [RelayCommand]
    private void Create() => CreateRequested?.Invoke(this, EventArgs.Empty);

    partial void OnSelectedEventChanged(EventSummary? value) => OpenCommand.NotifyCanExecuteChanged();
}
