using OrientDesk.BusinessLogic.Entities;
using OrientDesk.BusinessLogic.Enums;
using OrientDesk.BusinessLogic.Interfaces;
using OrientDesk.BusinessLogic.Models;

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

    private Guid CurrentDayId =>
        _session.CurrentDay?.Id
        ?? throw new InvalidOperationException("No competition day is currently selected.");

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

    public Task<IReadOnlyList<ControlPoint>> GetControlPointsAsync(CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null || _session.CurrentDay is null)
            return Task.FromResult<IReadOnlyList<ControlPoint>>([]);

        return _eventStore.GetControlPointsAsync(FolderPath, CurrentDayId, cancellationToken);
    }

    public async Task<ControlPoint> AddControlPointAsync(CancellationToken cancellationToken = default)
    {
        var dayId = CurrentDayId;
        var existing = await _eventStore.GetControlPointsAsync(FolderPath, dayId, cancellationToken);
        var nextOrder = existing.Count == 0 ? 1 : existing.Max(cp => cp.Order) + 1;

        var point = new ControlPoint
        {
            EventDayId = dayId,
            Order = nextOrder,
            Type = ControlPointType.Regular
        };
        await _eventStore.AddControlPointAsync(FolderPath, point, cancellationToken);
        return point;
    }

    public Task UpdateControlPointAsync(ControlPoint point, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(point);
        return _eventStore.UpdateControlPointAsync(FolderPath, point, cancellationToken);
    }

    public Task DeleteControlPointAsync(Guid pointId, CancellationToken cancellationToken = default)
        => _eventStore.DeleteControlPointAsync(FolderPath, pointId, cancellationToken);

    public async Task<ControlPointImportResult> ImportControlPointsAsync(
        IofCourseData data,
        bool replaceAll,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        var dayId = CurrentDayId;
        var folder = FolderPath;

        // Collapse duplicate codes within the file (a control may also appear as start/finish),
        // keeping the first occurrence so file order — and thus display order — is preserved.
        var parsed = new List<IofControl>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var control in data.Controls)
        {
            var code = control.Code.Trim();
            if (code.Length > 0 && seen.Add(code))
                parsed.Add(control);
        }

        if (replaceAll)
        {
            var points = new List<ControlPoint>(parsed.Count);
            for (var i = 0; i < parsed.Count; i++)
                points.Add(ToEntity(parsed[i], dayId, order: i + 1));

            await _eventStore.ReplaceControlPointsAsync(folder, dayId, points, cancellationToken);
            return new ControlPointImportResult(Imported: points.Count, Added: points.Count, Replaced: true);
        }

        // Add-only: append codes the day does not already have, numbering after the last one.
        var existing = await _eventStore.GetControlPointsAsync(folder, dayId, cancellationToken);
        var existingCodes = new HashSet<string>(
            existing.Select(cp => cp.Code.Trim()),
            StringComparer.OrdinalIgnoreCase);
        var nextOrder = existing.Count == 0 ? 1 : existing.Max(cp => cp.Order) + 1;

        var toAdd = new List<ControlPoint>();
        foreach (var control in parsed)
        {
            if (existingCodes.Contains(control.Code.Trim()))
                continue;
            toAdd.Add(ToEntity(control, dayId, order: nextOrder++));
        }

        await _eventStore.AddControlPointsAsync(folder, toAdd, cancellationToken);
        return new ControlPointImportResult(
            Imported: existing.Count + toAdd.Count,
            Added: toAdd.Count,
            Replaced: false);
    }

    private static ControlPoint ToEntity(IofControl control, Guid dayId, int order) => new()
    {
        EventDayId = dayId,
        Order = order,
        Code = control.Code.Trim(),
        Latitude = control.Latitude,
        Longitude = control.Longitude,
        Type = control.Type
    };
}
