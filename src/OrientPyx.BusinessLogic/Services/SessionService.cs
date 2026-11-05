using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.BusinessLogic.Models.Network;

namespace OrientPyx.BusinessLogic.Services;

/// <summary>
/// In-memory session holder. See <see cref="ISessionService"/> — runtime selection is a local
/// field; the app database only stores a pointer for startup restore.
/// <para>
/// Network mode: the pointer also records the role (local / station) and the host, so a station's pointer is only
/// restored after reconnecting to that host and never from the local events folder (<c>EnsureCreatedAsync</c> would
/// otherwise create an empty competition named after the host's). The service also listens to the change bus: when
/// another station (or the host) edits the current day's row or the competition metadata, the in-memory copies are
/// refreshed, so the discipline and the day lock pages read from the session never go stale.
/// </para>
/// </summary>
public sealed class SessionService : ISessionService
{
    private readonly IAppStore _appStore;
    private readonly IEventCatalogService _catalog;
    private readonly IEventStore _eventStore;
    private readonly IActivityLog _log;
    private readonly IWorkspaceMode _mode;
    private readonly INetworkClient? _station;

    // Serialises the bus-driven refreshes so two quick events cannot interleave their reads and writes.
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    public SessionService(
        IAppStore appStore,
        IEventCatalogService catalog,
        IEventStore eventStore,
        IActivityLog log,
        IWorkspaceMode mode,
        IDataChangeBus bus,
        INetworkClient? station = null)
    {
        _appStore = appStore;
        _catalog = catalog;
        _eventStore = eventStore;
        _log = log;
        _mode = mode;
        _station = station;
        bus.Changed += OnDataChanged;
    }

    public EventSummary? CurrentEvent { get; private set; }
    public EventDay? CurrentDay { get; private set; }
    public bool HasSelection => CurrentEvent is not null && CurrentDay is not null;

    public event EventHandler? SessionChanged;

    public async Task SelectAsync(EventSummary competition, EventDay day, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(competition);
        ArgumentNullException.ThrowIfNull(day);

        // Bring the event database up to the current schema before it is used. Migrations are only
        // applied on create otherwise, so a competition made before a new migration would be missing
        // the new tables until opened here.
        await _eventStore.EnsureCreatedAsync(competition.FolderPath, cancellationToken);

        // Move logging into this competition's own folder so its diagnostics live alongside its data.
        _log.UseEventFolder(competition.FolderPath);
        _log.Action($"Select competition '{competition.Identifier}', day {day.Number}");
        CurrentEvent = competition;
        CurrentDay = day;

        await SavePointerAsync(competition.Identifier, day.Number, cancellationToken);
        SessionChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task SetCurrentDayAsync(EventDay day, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(day);
        if (CurrentEvent is null)
            return;

        _log.Action($"Switch to day {day.Number}");
        CurrentDay = day;
        await SavePointerAsync(CurrentEvent.Identifier, day.Number, cancellationToken);
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

    public async Task RenameCurrentEventAsync(EventSummary competition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(competition);
        if (CurrentEvent is null || CurrentDay is null)
            return;

        CurrentEvent = competition;
        _log.UseEventFolder(competition.FolderPath);
        _log.Action($"Competition identifier changed to '{competition.Identifier}'");

        // The pointer stores the identifier, so a restart would otherwise look for the old folder name.
        await SavePointerAsync(competition.Identifier, CurrentDay.Number, cancellationToken);
        SessionChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool IsCurrentDayLocked => CurrentDay?.IsLocked ?? false;

    public void UpdateCurrentDay(EventDay day)
    {
        ArgumentNullException.ThrowIfNull(day);
        if (CurrentDay is null || CurrentDay.Id != day.Id)
            return;

        CurrentDay = day;
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
        var last = await _appStore.GetLastSessionAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(last.Identifier))
            return false;

        if (last.Mode == WorkspaceRole.Station)
        {
            // A station pointer names a competition on the HOST. Reconnect first; the catalog then lists the host's
            // competitions. Never fall back to the local folder — the identifier is not ours.
            if (_station is null || _mode.Role == WorkspaceRole.Host)
                return false;
            if (_mode.Role != WorkspaceRole.Station
                && !await _station.TryReconnectSavedAsync(TimeSpan.FromSeconds(5), cancellationToken))
                return false;
            if (!string.Equals(_mode.Station?.HostId, last.HostId, StringComparison.Ordinal))
                return false;
        }
        else if (_mode.Role == WorkspaceRole.Station)
        {
            // A local pointer while connected to a host: the host's list has nothing to do with it.
            return false;
        }

        var summary = await _catalog.FindByIdentifierAsync(last.Identifier, cancellationToken);
        if (summary is null)
            return false;

        // Apply any pending migrations to the restored event database before reading from it.
        await _eventStore.EnsureCreatedAsync(summary.FolderPath, cancellationToken);
        _log.UseEventFolder(summary.FolderPath);

        var days = await _eventStore.GetDaysAsync(summary.FolderPath, cancellationToken);
        if (days.Count == 0)
            return false;

        var day = days.FirstOrDefault(d => d.Number == last.DayNumber) ?? days[0];

        CurrentEvent = summary;
        CurrentDay = day;
        SessionChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private Task SavePointerAsync(string identifier, int dayNumber, CancellationToken cancellationToken)
    {
        var station = _mode.Role == WorkspaceRole.Station;
        return _appStore.SaveLastSessionAsync(
            new LastSession(identifier, dayNumber, station ? WorkspaceRole.Station : WorkspaceRole.Local,
                station ? _mode.Station?.HostId : null),
            cancellationToken);
    }

    // Another machine changed the current day's row or the competition metadata: refresh the in-memory copies
    // (UpdateCurrentDay / UpdateCurrentEvent raise SessionChanged, which every page already handles by reloading).
    // Our own writes are skipped — the page that made them already called the Update* method.
    private void OnDataChanged(object? sender, DataChange change)
    {
        if (change.Area is not (DataArea.Days or DataArea.Competition or DataArea.Any))
            return;
        if (string.Equals(change.Origin, _mode.SelfOrigin, StringComparison.Ordinal) && change.Area != DataArea.Any)
            return;
        if (CurrentEvent is not { } current || CurrentDay is null)
            return;
        if (change.Area != DataArea.Any && !SameCompetition(change.EventFolderPath, current.FolderPath))
            return;

        _ = RefreshFromStoreAsync(current, change.Area);
    }

    private async Task RefreshFromStoreAsync(EventSummary current, DataArea area)
    {
        if (!await _refreshGate.WaitAsync(0))
            return;
        try
        {
            if (area is DataArea.Days or DataArea.Any)
            {
                var days = await _eventStore.GetDaysAsync(current.FolderPath);
                var day = CurrentDay is { } cd ? days.FirstOrDefault(d => d.Id == cd.Id) : null;
                if (day is not null && CurrentDay is { } old && !SameDay(old, day))
                {
                    // SessionChanged makes every page reload; those reloads are a background refresh, not a user action.
                    BackgroundRefresh.QuietFor(TimeSpan.FromSeconds(3));
                    UpdateCurrentDay(day);
                }
            }

            if (area is DataArea.Competition or DataArea.Any)
            {
                var info = await _eventStore.GetCompetitionInfoAsync(current.FolderPath);
                if (info is not null && CurrentEvent is { } ev && ReferenceEquals(ev, current) && !SameSummary(ev, info))
                {
                    BackgroundRefresh.QuietFor(TimeSpan.FromSeconds(3));
                    UpdateCurrentEvent(new EventSummary
                    {
                        Identifier = ev.Identifier,
                        Name = info.Name,
                        Venue = info.Venue,
                        FolderPath = ev.FolderPath,
                        CreatedAt = info.CreatedAt,
                        DayCount = ev.DayCount,
                        StartDate = info.StartDate,
                        EndDate = info.EndDate,
                        IsHidden = info.IsHidden,
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _log.Error("Session refresh after a remote change failed", ex);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    // Compared by the folder name (the identifier): the dispatcher's path and the scanner's path can differ in form.
    private static bool SameCompetition(string eventPath, string sessionPath)
    {
        static string Key(string path) => Path.GetFileName(path.TrimEnd('/', '\\'));
        return string.Equals(eventPath, sessionPath, StringComparison.OrdinalIgnoreCase)
               || string.Equals(Key(eventPath), Key(sessionPath), StringComparison.OrdinalIgnoreCase);
    }

    private static bool SameDay(EventDay a, EventDay b)
        => a.Number == b.Number
           && a.IsLocked == b.IsLocked
           && a.DefaultDiscipline == b.DefaultDiscipline
           && a.Date == b.Date
           && string.Equals(a.Venue, b.Venue, StringComparison.Ordinal);

    private static bool SameSummary(EventSummary s, CompetitionInfo info)
        => string.Equals(s.Name, info.Name, StringComparison.Ordinal)
           && string.Equals(s.Venue, info.Venue, StringComparison.Ordinal)
           && s.StartDate == info.StartDate
           && s.EndDate == info.EndDate
           && s.IsHidden == info.IsHidden;
}
