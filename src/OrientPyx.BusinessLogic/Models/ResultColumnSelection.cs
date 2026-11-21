using System.Text.Json.Serialization;

namespace OrientPyx.BusinessLogic.Models;

/// <summary>
/// A user-chosen, ordered set of result columns — the column layout for one results surface (an online
/// publish, or one monitor file). Stored as a plain list of stable column keys (see
/// <see cref="ResultColumnInfo.Key"/>) in display order. A column not present in the list is hidden; an
/// unknown key (a column removed since the config was saved) is ignored on read.
/// </summary>
public sealed record ResultColumnSelection(
    [property: JsonPropertyName("keys")] IReadOnlyList<string> Keys)
{
    /// <summary>The layout version a selection is written with. 0 = saved before «Бали» became a column of its own
    /// (then «Очки» showed the score where a group had no points rule).</summary>
    public const int CurrentVersion = 1;

    /// <summary>The version the selection was saved with (absent in an older file = 0); see <see cref="Resolve"/>.</summary>
    [JsonPropertyName("v")]
    public int Version { get; init; }

    /// <summary>The default visible layout: place, name, bib, qualification, club, region, start, result,
    /// gap, relay running time, status, score, points. Mirrors the legacy monitor's columns plus the common online
    /// ones («Наростаючий» only shows on a screen with a relay group, «Бали» on one with a scored group).</summary>
    public static ResultColumnSelection Default { get; } = new(
    [
        "rk", "full_name", "bib", "qual", "club", "region",
        "start_time", "result_time", "gap", "cumulative", "status", "score", "points",
    ]) { Version = CurrentVersion };

    /// <summary>An empty selection (nothing chosen).</summary>
    public static ResultColumnSelection None { get; } = new([]);

    /// <summary>Resolves the keys to their column definitions in order, dropping unknown/duplicate keys. A selection
    /// saved before «Бали» existed (<see cref="Version"/> 0) that shows «Очки» gets «Бали» right after it: its «Очки»
    /// used to carry the score where a group had no points rule, so a rogaine screen keeps showing it.</summary>
    public IReadOnlyList<ResultColumnInfo> Resolve(IEnumerable<ResultColumnInfo>? extras = null)
    {
        var extraByKey = (extras ?? []).GroupBy(e => e.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ResultColumnInfo>(Keys.Count);
        foreach (var key in UpgradedKeys())
        {
            if (!seen.Add(key))
                continue;
            if (ResultColumnInfo.ByKey(key) is { } def)
                result.Add(def);
            else if (extraByKey.TryGetValue(key, out var extra))
                result.Add(extra);
        }
        return result;
    }

    // The keys with the version 0 → 1 upgrade applied (see Resolve).
    private IEnumerable<string> UpgradedKeys()
    {
        var addScore = Version < 1
                       && Keys.Contains("points", StringComparer.OrdinalIgnoreCase)
                       && !Keys.Contains("score", StringComparer.OrdinalIgnoreCase);
        foreach (var key in Keys)
        {
            yield return key;
            if (addScore && string.Equals(key, "points", StringComparison.OrdinalIgnoreCase))
                yield return "score";
        }
    }

    /// <summary>True when at least one valid column is selected.</summary>
    public bool HasAny => Resolve().Count > 0;
}
