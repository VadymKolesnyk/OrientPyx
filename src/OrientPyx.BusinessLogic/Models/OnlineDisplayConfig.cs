using System.Text.Json.Serialization;

namespace OrientPyx.BusinessLogic.Models;

/// <summary>
/// One column's online-display setting: its stable key (see <see cref="ResultColumnInfo.Key"/>) and whether it
/// is shown on the large (venue / desktop) screen and on the small (phone) screen. Mirrors the spectator
/// frontend's per-column <c>{ key, order, lg, sm }</c> shape (see <c>web/src/types.ts → ColumnConfig</c>); the
/// order comes from the column's position in <see cref="OnlineDisplayConfig.Columns"/>.
/// </summary>
public sealed record OnlineColumnConfig(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("lg")] bool Lg,
    [property: JsonPropertyName("sm")] bool Sm);

/// <summary>
/// The per-competition online column layout, richer than the flat <see cref="ResultColumnSelection"/> the
/// monitor uses: every column carries an independent large-screen / small-screen visibility, and the result /
/// status split can be toggled per screen. This is the .NET twin of the spectator frontend's
/// <c>DisplayConfig</c> (<c>web/src/types.ts</c>) — <see cref="Default"/> matches its
/// <c>DEFAULT_DISPLAY_CONFIG</c> exactly, so a never-configured competition renders identically to the
/// frontend's built-in default. Stored as JSON in the per-competition <see cref="OnlinePublishSettings"/>
/// and sent to the frontend as part of the events row's <c>display_config</c>.
/// </summary>
public sealed record OnlineDisplayConfig(
    [property: JsonPropertyName("columns")] IReadOnlyList<OnlineColumnConfig> Columns,
    /// <summary>Show a separate «Статус/DSQ» column on the large screen (otherwise the status/reason is folded
    /// into the «Результат» column).</summary>
    [property: JsonPropertyName("separateDsqLg")] bool SeparateDsqLg,
    /// <summary>Same, for the small (phone) screen.</summary>
    [property: JsonPropertyName("separateDsqSm")] bool SeparateDsqSm)
{
    /// <summary>The default layout — a 1:1 mirror of <c>DEFAULT_DISPLAY_CONFIG</c> in <c>web/src/types.ts</c>:
    /// place/name/region/result (and a relay's running time) on both screens, most others large-only, team (a
    /// rogaine's)/birth/qual off, and the DSQ split on for the large screen only. Keep in lock-step with the
    /// frontend default.</summary>
    public static OnlineDisplayConfig Default { get; } = new(
        [
            new("rk",          Lg: true,  Sm: true),
            new("full_name",   Lg: true,  Sm: true),
            new("bib",         Lg: true,  Sm: false),
            new("birth",       Lg: false, Sm: false),
            new("qual",        Lg: false, Sm: false),
            new("team",        Lg: false, Sm: false),
            new("club",        Lg: true,  Sm: false),
            new("region",      Lg: true,  Sm: true),
            new("start_time",  Lg: true,  Sm: false),
            new("result_time", Lg: true,  Sm: true),
            new("gap",         Lg: true,  Sm: false),
            new("cumulative",  Lg: true,  Sm: true),
            new("status",      Lg: true,  Sm: false),
            new("points",      Lg: true,  Sm: false),
        ],
        SeparateDsqLg: true,
        SeparateDsqSm: false);

    /// <summary>
    /// Resolves the config to its columns in order, dropping unknown/duplicate keys and pairing each with its
    /// definition + the two visibility flags. <paramref name="extras"/> are the extra columns (plugin columns and
    /// additional fields, §10.2) that can be shown: a config entry for one of them resolves to it (its definition's
    /// <see cref="ResultColumnInfo.LabelKey"/> is the extra's label); an entry for an extra not offered is skipped
    /// (see <see cref="Unresolved"/>). A catalogue column the config lacks (added since it was saved) takes its
    /// <see cref="Default"/> visibility, right after the column it follows there; an extra the config lacks is
    /// appended — a plugin column on the large screen, a field hidden.
    /// </summary>
    public IReadOnlyList<OnlineResolvedColumn> Resolve(IReadOnlyList<OnlineExtraColumn>? extras = null)
    {
        var extraByKey = new Dictionary<string, OnlineExtraColumn>(StringComparer.Ordinal);
        foreach (var e in extras ?? [])
            extraByKey.TryAdd(e.Key, e);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<OnlineResolvedColumn>(Columns.Count);
        foreach (var c in Columns)
        {
            if (!seen.Add(c.Key))
                continue;
            if (ResultColumnInfo.ByKey(c.Key) is { } def)
                result.Add(new OnlineResolvedColumn(def, c.Lg, c.Sm));
            else if (extraByKey.TryGetValue(c.Key, out var extra))
                result.Add(new OnlineResolvedColumn(ExtraDef(extra), c.Lg, c.Sm));
        }

        var defaults = Default.Columns.ToList();
        foreach (var def in ResultColumnInfo.All.Where(d => !seen.Contains(d.Key)))
        {
            var at = defaults.FindIndex(d => d.Key == def.Key);
            var flags = at >= 0 ? defaults[at] : null;
            // After the nearest column that precedes it in the default order; else at the end.
            var insert = result.Count;
            for (var i = at - 1; i >= 0; i--)
            {
                var before = result.FindIndex(r => string.Equals(r.Def.Key, defaults[i].Key, StringComparison.OrdinalIgnoreCase));
                if (before >= 0)
                {
                    insert = before + 1;
                    break;
                }
            }
            result.Insert(insert, new OnlineResolvedColumn(def, flags?.Lg ?? false, flags?.Sm ?? false));
            seen.Add(def.Key);
        }

        foreach (var extra in extraByKey.Values.Where(e => !seen.Contains(e.Key)))
            result.Add(new OnlineResolvedColumn(ExtraDef(extra), Lg: extra.IsPlugin, Sm: false));
        return result;
    }

    /// <summary>The config's entries for extra columns not among <paramref name="extras"/> (another day's plugin, a
    /// deleted field) — kept by an editor and saved back unchanged, so switching days loses no choice.</summary>
    public IReadOnlyList<OnlineColumnConfig> Unresolved(IReadOnlyList<OnlineExtraColumn>? extras)
    {
        var offered = (extras ?? []).Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
        return Columns
            .Where(c => ResultColumnInfo.ByKey(c.Key) is null && !offered.Contains(c.Key))
            .DistinctBy(c => c.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static ResultColumnInfo ExtraDef(OnlineExtraColumn extra) => new(ResultColumn.Extra, extra.Key, extra.Label);

    /// <summary>Builds a config from a legacy flat <see cref="ResultColumnSelection"/> (all chosen columns
    /// visible on both screens) so an old saved layout keeps working when <see cref="Default"/>'s richer
    /// model is introduced.</summary>
    public static OnlineDisplayConfig FromSelection(ResultColumnSelection selection) =>
        new(selection.Resolve().Select(d => new OnlineColumnConfig(d.Key, Lg: true, Sm: true)).ToList(),
            SeparateDsqLg: true, SeparateDsqSm: false);
}

/// <summary>A resolved online column: its definition plus its large/small-screen visibility.</summary>
public sealed record OnlineResolvedColumn(ResultColumnInfo Def, bool Lg, bool Sm)
{
    /// <summary>True when the column is shown on at least one screen (so it's part of the published set).</summary>
    public bool Visible => Lg || Sm;
}
