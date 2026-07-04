using System.Text.Json.Serialization;

namespace OrientPyx.BusinessLogic.Models;

/// <summary>
/// A user-chosen, ordered set of result columns — the column layout for one results surface (an online
/// publish, or one monitor file). Stored as a plain list of stable column keys (see
/// <see cref="ResultColumnDef.Key"/>) in display order. A column not present in the list is hidden; an
/// unknown key (a column removed since the config was saved) is ignored on read.
/// </summary>
public sealed record ResultColumnSelection(
    [property: JsonPropertyName("keys")] IReadOnlyList<string> Keys)
{
    /// <summary>The default visible layout: place, name, bib, qualification, club, region, start, result,
    /// gap, status, points. Mirrors the legacy monitor's columns plus the common online ones.</summary>
    public static ResultColumnSelection Default { get; } = new(
    [
        "rk", "full_name", "bib", "qual", "club", "region",
        "start_time", "result_time", "gap", "status", "points",
    ]);

    /// <summary>An empty selection (nothing chosen).</summary>
    public static ResultColumnSelection None { get; } = new([]);

    /// <summary>Resolves the keys to their column definitions in order, dropping unknown/duplicate keys.</summary>
    public IReadOnlyList<ResultColumnDef> Resolve()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ResultColumnDef>(Keys.Count);
        foreach (var key in Keys)
        {
            if (!seen.Add(key))
                continue;
            if (ResultColumnDef.ByKey(key) is { } def)
                result.Add(def);
        }
        return result;
    }

    /// <summary>True when at least one valid column is selected.</summary>
    public bool HasAny => Resolve().Count > 0;
}
