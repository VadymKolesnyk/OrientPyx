using OrientPyx.Presentation.Controls;

namespace OrientPyx.Presentation.Services;

/// <summary>
/// Reads/writes table view layouts (column order, width, visibility) as plain JSON files, in two layers:
/// <list type="bullet">
/// <item>the <b>per-competition</b> layer — <c>events/&lt;id&gt;/views.json</c>, written automatically on
/// every hide/reorder/resize. No-ops when no competition is selected;</item>
/// <item>the <b>application default</b> layer — <c>views-default.json</c> in the data root, written only when
/// the user explicitly picks "save this view for the next competitions" on one table. A competition with no
/// saved view of its own seeds from it, so a preferred column order carries into new competitions.</item>
/// </list>
/// Both files hold every table's layout keyed by a stable table id, so one table's default never disturbs
/// another's.
/// </summary>
public interface ITableLayoutStore
{
    /// <summary>
    /// A stable id for the competition the layouts currently belong to (its folder path), or null when
    /// none is selected. A table watches this to reset and reload its cached layout when the competition
    /// changes (its in-memory order/width/hidden must not carry over to another competition).
    /// </summary>
    string? CurrentScopeId { get; }

    /// <summary>
    /// Loads the effective layout for <paramref name="tableKey"/>: the competition's own saved view, or —
    /// when it has none (a fresh competition, or none selected) — the application default. Null when
    /// neither layer has one.
    /// </summary>
    TableLayout? Load(string tableKey);

    /// <summary>Saves the layout for <paramref name="tableKey"/> (read-modify-write of the shared file). No-op when no competition.</summary>
    void Save(string tableKey, TableLayout layout);

    /// <summary>
    /// Stores <paramref name="layout"/> as the application-wide default for <paramref name="tableKey"/>, so
    /// new competitions start from it. Works with no competition selected.
    /// </summary>
    void SaveDefault(string tableKey, TableLayout layout);

    /// <summary>
    /// Drops this table's saved view in <b>both</b> layers, so it falls back to the layout its view builds
    /// by default.
    /// </summary>
    void ClearLayout(string tableKey);

    /// <summary>True when an application default has been saved for <paramref name="tableKey"/>.</summary>
    bool HasDefault(string tableKey);
}
