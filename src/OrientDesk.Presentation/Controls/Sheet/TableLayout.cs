using System.Collections.Generic;

namespace OrientDesk.Presentation.Controls;

/// <summary>
/// A persisted view of one <see cref="SheetTable"/>: which columns are hidden, the band order, and
/// per-column widths. Serialized to <c>events/&lt;id&gt;/views.json</c> (one entry per table id) and
/// reloaded when the table is built. Plain DTO — keyed by <see cref="SheetColumn.Key"/> (columns) and
/// band signature (order), both stable across rebuilds, so an unknown/missing key just falls back to
/// the build default.
/// </summary>
public sealed class TableLayout
{
    /// <summary>Per-column saved state, keyed by <see cref="SheetColumn.Key"/>.</summary>
    public Dictionary<string, ColumnLayout> Columns { get; set; } = new();

    /// <summary>The band order as <c>Signature(band)</c> strings; empty = build default order.</summary>
    public List<string> Order { get; set; } = new();

    /// <summary>The keys of hidden columns.</summary>
    public List<string> Hidden { get; set; } = new();
}

/// <summary>One column's persisted state.</summary>
public sealed class ColumnLayout
{
    /// <summary>The user-set pixel width; null = use the build default.</summary>
    public double? Width { get; set; }
}
