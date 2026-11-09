using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Services;

/// <summary>
/// Keeps a competition's copy of its points rules (<see cref="CompetitionInfo.PointsRulesJson"/>, see
/// <see cref="PointsRuleCopies"/>) in step with the catalogue the results are computed with — through
/// <see cref="IResultsAppData"/>, so on a station it is the host's. Writes only when the copy actually changes.
/// </summary>
public sealed class PointsRuleCopySync
{
    private readonly IEventStore _eventStore;
    private readonly IResultsAppData _rules;

    public PointsRuleCopySync(IEventStore eventStore, IResultsAppData rules)
    {
        _eventStore = eventStore;
        _rules = rules;
    }

    /// <summary>
    /// Rewrites the copy for every rule the competition references (default + group overrides on every day):
    /// refreshed from the catalogue where it knows the id, kept as it is where it does not, dropped when unreferenced.
    /// Run when a competition is opened and after a rule is edited.
    /// </summary>
    public async Task RefreshAsync(string eventFolderPath, CancellationToken cancellationToken = default)
    {
        var info = await _eventStore.GetCompetitionInfoAsync(eventFolderPath, cancellationToken);
        if (info is null)
            return;

        var settings = new List<GroupDaySettings>();
        foreach (var day in await _eventStore.GetDaysAsync(eventFolderPath, cancellationToken))
            settings.AddRange(await _eventStore.GetGroupDaySettingsAsync(eventFolderPath, day.Id, cancellationToken));

        var referenced = PointsRuleCopies.ReferencedIds(info, settings);
        var catalogue = referenced.Count == 0
            ? []
            : await _rules.GetPointsRulesAsync(cancellationToken);
        var json = PointsRuleCopies.Refresh(info.PointsRulesJson, catalogue, referenced);
        await SaveIfChangedAsync(eventFolderPath, info, json, cancellationToken);
    }

    /// <summary>
    /// Makes sure the copy holds <paramref name="ruleId"/> as the catalogue has it — the cheap path after a group or
    /// the competition default is pointed at a rule. A rule the catalogue does not know (one picked from the copy)
    /// is already in the copy, so nothing changes.
    /// </summary>
    public async Task EnsureAsync(string eventFolderPath, Guid? ruleId, CancellationToken cancellationToken = default)
    {
        if (ruleId is not { } id || id == Guid.Empty)
            return;

        var rule = (await _rules.GetPointsRulesAsync(cancellationToken)).FirstOrDefault(r => r.Id == id);
        if (rule is null)
            return;

        var info = await _eventStore.GetCompetitionInfoAsync(eventFolderPath, cancellationToken);
        if (info is null)
            return;

        var json = PointsRuleCopies.Upsert(info.PointsRulesJson, rule);
        await SaveIfChangedAsync(eventFolderPath, info, json, cancellationToken);
    }

    private Task SaveIfChangedAsync(string eventFolderPath, CompetitionInfo info, string? json, CancellationToken cancellationToken)
        => string.Equals(json, string.IsNullOrWhiteSpace(info.PointsRulesJson) ? null : info.PointsRulesJson, StringComparison.Ordinal)
            ? Task.CompletedTask
            : _eventStore.SetPointsRulesJsonAsync(eventFolderPath, json, cancellationToken);
}
