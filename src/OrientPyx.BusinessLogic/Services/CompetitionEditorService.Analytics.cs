using OrientPyx.BusinessLogic.Models.Mcp;

namespace OrientPyx.BusinessLogic.Services;

public sealed partial class CompetitionEditorService
{
    public async Task<AnalyticsDayData?> GetAnalyticsDayDataAsync(Guid dayId, CancellationToken cancellationToken = default)
    {
        if (CurrentEvent is null)
            return null;

        var folder = FolderPath;
        var snapshot = await _eventStore.GetDaySnapshotAsync(folder, dayId, cancellationToken);
        var day = snapshot.Days.FirstOrDefault(d => d.Id == dayId)
            ?? (await _eventStore.GetDaysAsync(folder, cancellationToken)).FirstOrDefault(d => d.Id == dayId);
        if (day is null)
            return null;

        // The results and passages exactly as the protocols and the split export compute them, from this one snapshot.
        var results = await ComputeDayResultsAsync(folder, day, cancellationToken, snapshot: snapshot);
        var settingsByGroup = snapshot.GroupDaySettings
            .GroupBy(s => s.GroupId)
            .ToDictionary(g => g.Key, g => g.First());
        var inputs = BuildDaySplitInputs(snapshot.ControlPoints);
        var variantsByGroup = await LoadCourseVariantsAsync(
            folder, dayId, settingsByGroup, day.DefaultDiscipline, inputs.StartFinishCodes, inputs.DisabledCodes, cancellationToken);
        var splits = BuildDaySplitViews(
            day.DefaultDiscipline, snapshot.Links, snapshot.Participants, settingsByGroup, inputs, snapshot.FinishReadouts,
            variantsByGroup, results);

        return new AnalyticsDayData(day, snapshot, results, splits);
    }
}
