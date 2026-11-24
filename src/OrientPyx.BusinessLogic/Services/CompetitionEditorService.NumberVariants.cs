using OrientPyx.BusinessLogic.Models;
using OrientPyx.BusinessLogic.Services.Relay;

namespace OrientPyx.BusinessLogic.Services;

// «Варіанти за номерами»: the day's number → variation table (the maps to hand out), plus the numbers still without one.
public sealed partial class CompetitionEditorService
{
    public async Task<NumberVariantsData> GetNumberVariantsAsync(CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is null || _session.CurrentDay is null)
            return NumberVariantsData.Empty;

        var day = _session.CurrentDay;
        var snapshot = await _eventStore.GetDaySnapshotAsync(FolderPath, day.Id, cancellationToken);
        return NumberVariantRows.Build(day, snapshot, IsRelay);
    }
}
