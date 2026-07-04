using OrientPyx.Presentation.Controls;

namespace OrientPyx.Presentation.Services;

/// <summary>
/// Reads/writes per-competition table view layouts (column order, width, visibility) as a plain JSON
/// file in the current competition's folder (<c>events/&lt;id&gt;/views.json</c>). One file holds every
/// table's layout, keyed by a stable table id. No-ops (returns null / does nothing) when no competition
/// is selected, so app-level tables simply don't persist.
/// </summary>
public interface ITableLayoutStore
{
    /// <summary>
    /// A stable id for the competition the layouts currently belong to (its folder path), or null when
    /// none is selected. A table watches this to reset and reload its cached layout when the competition
    /// changes (its in-memory order/width/hidden must not carry over to another competition).
    /// </summary>
    string? CurrentScopeId { get; }

    /// <summary>Loads the saved layout for <paramref name="tableKey"/>, or null when none/no competition.</summary>
    TableLayout? Load(string tableKey);

    /// <summary>Saves the layout for <paramref name="tableKey"/> (read-modify-write of the shared file). No-op when no competition.</summary>
    void Save(string tableKey, TableLayout layout);
}
