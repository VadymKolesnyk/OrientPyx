using OrientDesk.BusinessLogic.Entities;
using OrientDesk.BusinessLogic.Interfaces;

namespace OrientDesk.BusinessLogic.Services;

/// <summary>
/// Default <see cref="ICompetitionEditorService"/>: resolves the current competition's folder
/// from the session and delegates persistence to <see cref="IEventStore"/>.
/// </summary>
public sealed class CompetitionEditorService : ICompetitionEditorService
{
    private readonly ISessionService _session;
    private readonly IEventStore _eventStore;

    public CompetitionEditorService(ISessionService session, IEventStore eventStore)
    {
        _session = session;
        _eventStore = eventStore;
    }

    private string FolderPath =>
        _session.CurrentEvent?.FolderPath
        ?? throw new InvalidOperationException("No competition is currently selected.");

    public Task<CompetitionInfo?> GetInfoAsync(CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null)
            return Task.FromResult<CompetitionInfo?>(null);

        return _eventStore.GetCompetitionInfoAsync(FolderPath, cancellationToken);
    }

    public Task SaveInfoAsync(CompetitionInfo info, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(info);
        return _eventStore.SaveCompetitionInfoAsync(FolderPath, info, cancellationToken);
    }

    public Task<IReadOnlyList<EventDay>> GetDaysAsync(CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null)
            return Task.FromResult<IReadOnlyList<EventDay>>([]);

        return _eventStore.GetDaysAsync(FolderPath, cancellationToken);
    }

    public async Task<EventDay> AddDayAsync(CancellationToken cancellationToken = default)
    {
        var days = await _eventStore.GetDaysAsync(FolderPath, cancellationToken);
        var nextNumber = days.Count == 0 ? 1 : days.Max(d => d.Number) + 1;

        var day = new EventDay { Number = nextNumber };
        await _eventStore.AddDayAsync(FolderPath, day, cancellationToken);
        return day;
    }

    public Task UpdateDayAsync(EventDay day, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(day);
        return _eventStore.UpdateDayAsync(FolderPath, day, cancellationToken);
    }

    public Task DeleteDayAsync(Guid dayId, CancellationToken cancellationToken = default)
        => _eventStore.DeleteDayAsync(FolderPath, dayId, cancellationToken);
}
