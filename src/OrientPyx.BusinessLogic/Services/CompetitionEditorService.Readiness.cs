using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.BusinessLogic.Services.Readiness;

namespace OrientPyx.BusinessLogic.Services;

// «Перевірка готовності дня»: the day's problems in one list (DayReadinessChecker), also counted on the dashboard.
public sealed partial class CompetitionEditorService
{
    public async Task<DayReadiness> GetDayReadinessAsync(Guid dayId, CancellationToken cancellationToken = default)
    {
        if (CurrentEvent is null)
            return DayReadiness.Empty;

        var folder = FolderPath;
        var day = (await _eventStore.GetDaysAsync(folder, cancellationToken)).FirstOrDefault(d => d.Id == dayId);
        if (day is null)
            return DayReadiness.Empty;

        var snapshot = await _eventStore.GetDaySnapshotAsync(folder, day.Id, cancellationToken);
        return await BuildReadinessAsync(folder, day, snapshot, cancellationToken);
    }

    // The check over a snapshot the caller read (the dashboard reads one for its counts too). The additional fields'
    // definitions are not in the snapshot; the chip-capacity check follows this machine's «Тип відмітки», as the
    // participant tables do.
    private async Task<DayReadiness> BuildReadinessAsync(
        string folder, EventDay day, EventDaySnapshot snapshot, CancellationToken cancellationToken)
    {
        var fields = (await _eventStore.GetCustomFieldDefsAsync(folder, cancellationToken)).Select(CustomField.From).ToList();
        var sportIdent = ReadoutTypes.FromStored(await _appStore.GetReadoutTypeAsync(cancellationToken)) == ReadoutType.SportIdent;
        return _readiness.Check(new DayReadinessInput(day, snapshot, fields, sportIdent));
    }
}
