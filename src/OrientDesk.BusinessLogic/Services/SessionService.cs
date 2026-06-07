using OrientDesk.BusinessLogic.Entities;
using OrientDesk.BusinessLogic.Interfaces;
using OrientDesk.BusinessLogic.Models;

namespace OrientDesk.BusinessLogic.Services;

/// <summary>
/// In-memory session holder. See <see cref="ISessionService"/> — runtime selection is a local
/// field; the app database only stores a pointer for startup restore.
/// </summary>
public sealed class SessionService : ISessionService
{
    private readonly IAppStore _appStore;
    private readonly IEventCatalogService _catalog;
    private readonly IEventStore _eventStore;

    public SessionService(IAppStore appStore, IEventCatalogService catalog, IEventStore eventStore)
    {
        _appStore = appStore;
        _catalog = catalog;
        _eventStore = eventStore;
    }

    public EventSummary? CurrentEvent { get; private set; }
    public EventDay? CurrentDay { get; private set; }
    public bool HasSelection => CurrentEvent is not null && CurrentDay is not null;

    public event EventHandler? SessionChanged;

    public async Task SelectAsync(EventSummary competition, EventDay day, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(competition);
        ArgumentNullException.ThrowIfNull(day);

        CurrentEvent = competition;
        CurrentDay = day;

        await _appStore.SaveLastSessionAsync(competition.Identifier, day.Number, cancellationToken);
        SessionChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task SetCurrentDayAsync(EventDay day, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(day);
        if (CurrentEvent is null)
            return;

        CurrentDay = day;
        await _appStore.SaveLastSessionAsync(CurrentEvent.Identifier, day.Number, cancellationToken);
        SessionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void UpdateCurrentEvent(EventSummary competition)
    {
        ArgumentNullException.ThrowIfNull(competition);
        if (CurrentEvent is null)
            return;

        CurrentEvent = competition;
        SessionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        CurrentEvent = null;
        CurrentDay = null;
        SessionChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<bool> TryRestoreLastAsync(CancellationToken cancellationToken = default)
    {
        var (identifier, dayNumber) = await _appStore.GetLastSessionAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(identifier))
            return false;

        var summary = await _catalog.FindByIdentifierAsync(identifier, cancellationToken);
        if (summary is null)
            return false;

        var days = await _eventStore.GetDaysAsync(summary.FolderPath, cancellationToken);
        if (days.Count == 0)
            return false;

        var day = days.FirstOrDefault(d => d.Number == dayNumber) ?? days[0];

        CurrentEvent = summary;
        CurrentDay = day;
        SessionChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }
}
