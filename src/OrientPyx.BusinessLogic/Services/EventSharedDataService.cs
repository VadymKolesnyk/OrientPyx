using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Services;

public sealed class EventSharedDataService : IEventSharedDataService
{
    private readonly IAppStore _appStore;
    private readonly IEventStore _eventStore;

    public EventSharedDataService(IAppStore appStore, IEventStore eventStore)
    {
        _appStore = appStore;
        _eventStore = eventStore;
    }

    public async Task<EventSharedData> CollectAsync(string eventFolderPath, CancellationToken cancellationToken = default)
    {
        var ruleIds = new HashSet<Guid>();
        var info = await _eventStore.GetCompetitionInfoAsync(eventFolderPath, cancellationToken);
        if (info?.DefaultPointsRuleId is { } defaultId)
            ruleIds.Add(defaultId);
        foreach (var day in await _eventStore.GetDaysAsync(eventFolderPath, cancellationToken))
        {
            foreach (var s in await _eventStore.GetGroupDaySettingsAsync(eventFolderPath, day.Id, cancellationToken))
            {
                if (s.PointsRuleId is { } id)
                    ruleIds.Add(id);
            }
        }

        var rules = (await _appStore.GetPointsRulesAsync(cancellationToken))
            .Where(r => ruleIds.Contains(r.Id))
            .Select(r => new SharedPointsRule(r.Id, r.Name, r.Kind, r.TableJson, r.Formula))
            .ToList();

        var rankNames = (await _eventStore.GetParticipantsAsync(eventFolderPath, cancellationToken))
            .Select(p => (p.Rank ?? string.Empty).Trim())
            .Where(n => n.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ranks = (await _appStore.GetRanksAsync(cancellationToken))
            .Where(r => rankNames.Contains(r.Name.Trim()))
            .Select(r => new SharedRank(r.Name, r.Points))
            .ToList();

        return new EventSharedData(rules, ranks);
    }

    public async Task<EventSharedDataImportResult> ApplyAsync(string eventFolderPath, EventSharedData data, CancellationToken cancellationToken = default)
    {
        var (added, remap) = await ImportPointsRulesAsync(data.PointsRules, cancellationToken);
        if (remap.Count > 0)
            await RemapPointsRulesAsync(eventFolderPath, remap, cancellationToken);
        var ranksAdded = await ImportRanksAsync(data.Ranks, cancellationToken);
        return new EventSharedDataImportResult(added, remap.Count, ranksAdded);
    }

    // Returns how many rules were added and the archive-id → local-id map for rules matched by content.
    private async Task<(int Added, Dictionary<Guid, Guid> Remap)> ImportPointsRulesAsync(
        IReadOnlyList<SharedPointsRule> rules, CancellationToken cancellationToken)
    {
        var local = (await _appStore.GetPointsRulesAsync(cancellationToken)).ToList();
        var remap = new Dictionary<Guid, Guid>();
        var added = 0;

        foreach (var rule in rules)
        {
            if (local.Any(r => r.Id == rule.Id))
                continue;

            // Seeded rules get a fresh id on every install, so the same rule usually exists under another id.
            var same = local.Where(r => SameDefinition(r, rule)).ToList();
            var match = same.FirstOrDefault(r => string.Equals(r.Name, rule.Name, StringComparison.OrdinalIgnoreCase))
                        ?? same.FirstOrDefault();
            if (match is not null)
            {
                remap[rule.Id] = match.Id;
                continue;
            }

            var entity = new PointsRule
            {
                Id = rule.Id,
                Name = UniqueName(rule.Name, local.Select(r => r.Name)),
                Kind = rule.Kind,
                TableJson = rule.TableJson,
                Formula = rule.Formula,
            };
            await _appStore.AppendPointsRuleAsync(entity, cancellationToken);
            local.Add(entity);
            added++;
        }

        return (added, remap);
    }

    private async Task RemapPointsRulesAsync(string eventFolderPath, Dictionary<Guid, Guid> remap, CancellationToken cancellationToken)
    {
        var info = await _eventStore.GetCompetitionInfoAsync(eventFolderPath, cancellationToken);
        if (info?.DefaultPointsRuleId is { } defaultId && remap.TryGetValue(defaultId, out var newDefault))
        {
            info.DefaultPointsRuleId = newDefault;
            await _eventStore.SaveCompetitionInfoAsync(eventFolderPath, info, cancellationToken);
        }

        foreach (var day in await _eventStore.GetDaysAsync(eventFolderPath, cancellationToken))
        {
            foreach (var s in await _eventStore.GetGroupDaySettingsAsync(eventFolderPath, day.Id, cancellationToken))
            {
                if (s.PointsRuleId is { } id && remap.TryGetValue(id, out var newId))
                {
                    s.PointsRuleId = newId;
                    await _eventStore.UpdateGroupDaySettingsAsync(eventFolderPath, s, cancellationToken);
                }
            }
        }
    }

    private async Task<int> ImportRanksAsync(IReadOnlyList<SharedRank> ranks, CancellationToken cancellationToken)
    {
        var names = (await _appStore.GetRanksAsync(cancellationToken))
            .Select(r => r.Name.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var rank in ranks)
        {
            var name = rank.Name.Trim();
            if (name.Length == 0 || !names.Add(name))
                continue;
            await _appStore.AppendRankAsync(new SportRank { Name = name, Points = rank.Points }, cancellationToken);
            added++;
        }
        return added;
    }

    private static bool SameDefinition(PointsRule local, SharedPointsRule rule)
    {
        if (local.Kind != rule.Kind)
            return false;
        return rule.Kind == PointsRuleKind.Table
            ? PointsTable.Parse(local.TableJson).SequenceEqual(PointsTable.Parse(rule.TableJson))
            : string.Equals(StripSpaces(local.Formula), StripSpaces(rule.Formula), StringComparison.Ordinal);
    }

    private static string StripSpaces(string? s) => string.Concat((s ?? string.Empty).Where(c => !char.IsWhiteSpace(c)));

    // Rule names are unique; an imported rule whose name is taken gets a " (2)", " (3)", … suffix.
    private static string UniqueName(string name, IEnumerable<string> taken)
    {
        name = (name ?? string.Empty).Trim();
        if (name.Length == 0)
            return name;
        var set = taken.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!set.Contains(name))
            return name;
        for (var i = 2; ; i++)
        {
            var candidate = $"{name} ({i})";
            if (!set.Contains(candidate))
                return candidate;
        }
    }
}
