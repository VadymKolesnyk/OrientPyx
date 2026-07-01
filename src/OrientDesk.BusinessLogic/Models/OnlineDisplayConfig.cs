using System.Text.Json.Serialization;

namespace OrientDesk.BusinessLogic.Models;

/// <summary>
/// One column's online-display setting: its stable key (see <see cref="ResultColumnDef.Key"/>) and whether it
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
    /// place/name/result on both screens, most extras large-only, birth/qual off, and the DSQ split on for the
    /// large screen only. Keep in lock-step with the frontend default.</summary>
    public static OnlineDisplayConfig Default { get; } = new(
        [
            new("rk",          Lg: true,  Sm: true),
            new("full_name",   Lg: true,  Sm: true),
            new("bib",         Lg: true,  Sm: false),
            new("birth",       Lg: false, Sm: false),
            new("qual",        Lg: false, Sm: false),
            new("team",        Lg: true,  Sm: true),
            new("club",        Lg: true,  Sm: false),
            new("start_time",  Lg: true,  Sm: false),
            new("result_time", Lg: true,  Sm: true),
            new("gap",         Lg: true,  Sm: false),
            new("status",      Lg: true,  Sm: false),
            new("points",      Lg: true,  Sm: false),
        ],
        SeparateDsqLg: true,
        SeparateDsqSm: false);

    /// <summary>Resolves the config to its columns in order, dropping unknown/duplicate keys and pairing each
    /// with its definition + the two visibility flags. Any catalogue column not present in the config is
    /// appended hidden (lg=sm=false), so a config saved before a column existed still surfaces it as "off".</summary>
    public IReadOnlyList<OnlineResolvedColumn> Resolve()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<OnlineResolvedColumn>(Columns.Count);
        foreach (var c in Columns)
        {
            if (!seen.Add(c.Key))
                continue;
            if (ResultColumnDef.ByKey(c.Key) is { } def)
                result.Add(new OnlineResolvedColumn(def, c.Lg, c.Sm));
        }
        foreach (var def in ResultColumnDef.All.Where(d => !seen.Contains(d.Key)))
            result.Add(new OnlineResolvedColumn(def, Lg: false, Sm: false));
        return result;
    }

    /// <summary>Builds a config from a legacy flat <see cref="ResultColumnSelection"/> (all chosen columns
    /// visible on both screens) so an old saved layout keeps working when <see cref="Default"/>'s richer
    /// model is introduced.</summary>
    public static OnlineDisplayConfig FromSelection(ResultColumnSelection selection) =>
        new(selection.Resolve().Select(d => new OnlineColumnConfig(d.Key, Lg: true, Sm: true)).ToList(),
            SeparateDsqLg: true, SeparateDsqSm: false);
}

/// <summary>A resolved online column: its definition plus its large/small-screen visibility.</summary>
public sealed record OnlineResolvedColumn(ResultColumnDef Def, bool Lg, bool Sm)
{
    /// <summary>True when the column is shown on at least one screen (so it's part of the published set).</summary>
    public bool Visible => Lg || Sm;
}
