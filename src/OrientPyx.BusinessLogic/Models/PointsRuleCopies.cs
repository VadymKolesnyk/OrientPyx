using System.Text.Json;
using OrientPyx.BusinessLogic.Entities;

namespace OrientPyx.BusinessLogic.Models;

/// <summary>
/// The competition's own copy of the points rules it references (<see cref="CompetitionInfo.PointsRulesJson"/>).
/// <para>
/// Points rules live in each machine's app.db and a rule's id is minted there — the seeded ones get a fresh id on
/// every install. A competition stores only the id, so the same competition opened by another instance (another
/// install, the dev build, another computer) would reference ids that instance does not know and award nothing.
/// The copy closes that gap: a rule resolves from this machine's catalogue first (so editing it on «Очки» takes
/// effect at once) and falls back to the competition's copy, so every instance scores the same.
/// </para>
/// </summary>
public static class PointsRuleCopies
{
    private sealed record Entry(Guid Id, string? Name, PointsRuleKind Kind, string? TableJson, string? Formula);

    /// <summary>The stored copies; blank or unreadable JSON → none.</summary>
    public static IReadOnlyList<PointsRule> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            var entries = JsonSerializer.Deserialize<List<Entry>>(json) ?? [];
            var seen = new HashSet<Guid>();
            return entries
                .Where(e => e is not null && e.Id != Guid.Empty && seen.Add(e.Id))
                .Select(e => new PointsRule
                {
                    Id = e.Id,
                    Name = e.Name ?? string.Empty,
                    Kind = e.Kind,
                    TableJson = e.TableJson,
                    Formula = e.Formula,
                })
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The JSON for <paramref name="rules"/> (ordered by id, so equal sets give equal text); none → null.</summary>
    public static string? Serialize(IEnumerable<PointsRule> rules)
    {
        var entries = rules
            .OrderBy(r => r.Id)
            .Select(r => new Entry(r.Id, r.Name, r.Kind, r.TableJson, r.Formula))
            .ToList();
        return entries.Count == 0 ? null : JsonSerializer.Serialize(entries);
    }

    /// <summary>
    /// The rules a results computation may use: the catalogue, plus each copy whose id the catalogue lacks
    /// (the catalogue wins on a shared id).
    /// </summary>
    public static IReadOnlyList<PointsRule> WithCopies(IReadOnlyList<PointsRule> catalogue, string? json)
    {
        var copies = Parse(json);
        if (copies.Count == 0)
            return catalogue;
        var known = catalogue.Select(r => r.Id).ToHashSet();
        var missing = copies.Where(c => !known.Contains(c.Id)).ToList();
        return missing.Count == 0 ? catalogue : [.. catalogue, .. missing];
    }

    /// <summary>The rule ids the competition references: its default and every group override («немає» excluded).</summary>
    public static HashSet<Guid> ReferencedIds(CompetitionInfo? info, IEnumerable<GroupDaySettings> settings)
    {
        var ids = new HashSet<Guid>();
        if (info?.DefaultPointsRuleId is { } defaultId && defaultId != Guid.Empty)
            ids.Add(defaultId);
        foreach (var s in settings)
            if (s.PointsRuleId is { } id && id != Guid.Empty)
                ids.Add(id);
        return ids;
    }

    /// <summary>
    /// The copy for exactly the <paramref name="referenced"/> ids: taken from the catalogue where it knows the id,
    /// else the existing copy is kept (another instance wrote it); copies no longer referenced are dropped.
    /// </summary>
    public static string? Refresh(string? json, IReadOnlyList<PointsRule> catalogue, IReadOnlySet<Guid> referenced)
    {
        var existing = Parse(json).ToDictionary(r => r.Id);
        var byId = catalogue.GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First());
        var copies = new List<PointsRule>();
        foreach (var id in referenced)
        {
            if (byId.TryGetValue(id, out var rule) || existing.TryGetValue(id, out rule))
                copies.Add(rule);
        }
        return Serialize(copies);
    }

    /// <summary>Adds or replaces the copy of one rule, leaving the others as they are.</summary>
    public static string? Upsert(string? json, PointsRule rule)
    {
        var copies = Parse(json).Where(r => r.Id != rule.Id).ToList();
        copies.Add(rule);
        return Serialize(copies);
    }
}

/// <summary>
/// A points rule offered for a competition: one from the catalogue the results are computed with (this machine's,
/// or the host's on a station), or — <see cref="IsCompetitionCopy"/> — known only from the competition's own copy
/// (made by another instance, or deleted here since).
/// </summary>
public sealed record PointsRuleChoice(PointsRule Rule, bool IsCompetitionCopy);
