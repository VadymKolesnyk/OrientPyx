using System.Text.Json.Serialization;

namespace OrientDesk.BusinessLogic.Models;

/// <summary>
/// Per-competition configuration for the on-screen results monitor («Результати на монітор»): a set of
/// output HTML files (each a chosen subset of the day's groups + its own title / auto-scroll / auto-refresh
/// timing) plus ONE column layout shared by every file. The app regenerates every enabled file on an interval
/// from the day's computed results; the generated page reloads itself and smoothly scrolls, so it can be
/// opened straight from disk (file://) on a venue screen — the modern successor to the legacy orientir.exe
/// <c>rezNN.htm</c> screens. Stored as JSON in the event database; the active day is the session day.
/// </summary>
public sealed record MonitorSettings(
    [property: JsonPropertyName("files")] IReadOnlyList<MonitorFile> Files,
    /// <summary>The column layout shared by ALL files (order + visibility). One config for the whole monitor
    /// so every rezNN page shows the same columns. Null/empty falls back to the default set.</summary>
    [property: JsonPropertyName("columns")] ResultColumnSelection? Columns = null)
{
    /// <summary>An empty configuration (no files yet).</summary>
    public static MonitorSettings Empty { get; } = new([]);

    /// <summary>The files that are switched on and have a file name — the ones a publish tick writes.</summary>
    public IReadOnlyList<MonitorFile> ActiveFiles =>
        Files.Where(f => f.Enabled && !string.IsNullOrWhiteSpace(f.Path)).ToList();

    /// <summary>The effective shared column layout — the saved selection, or the default when none/empty.
    /// Older configs stored the columns per file; when the shared value is absent we seed it from the first
    /// file that has one, so an existing setup keeps its columns after the upgrade.</summary>
    public ResultColumnSelection EffectiveColumns =>
        Columns is { } c && c.HasAny ? c
        : Files.Select(f => f.Columns).FirstOrDefault(sel => sel is { HasAny: true }) is { } legacy ? legacy
        : ResultColumnSelection.Default;
}

/// <summary>
/// One monitor output file: its file name (written into the competition's <c>monitor</c> output folder),
/// which groups it shows, and its auto-scroll / auto-refresh timing. The column layout is NOT per file — it
/// is shared across all files on <see cref="MonitorSettings"/>. <see cref="Path"/> holds the file name only —
/// the directory is fixed per competition. <see cref="GroupNames"/> empty means "all groups of the day".
/// (The JSON key stays <c>path</c> for backward compatibility with earlier saves.)
/// </summary>
public sealed record MonitorFile(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("groups")] IReadOnlyList<string> GroupNames,
    /// <summary>Legacy per-file column layout — kept only to read old configs and seed the now-shared
    /// <see cref="MonitorSettings.Columns"/>. New saves leave this null.</summary>
    [property: JsonPropertyName("columns")] ResultColumnSelection? Columns,
    /// <summary>Seconds between data refreshes — how often the page reloads itself. Min 3.</summary>
    [property: JsonPropertyName("refreshSeconds")] int RefreshSeconds,
    /// <summary>Pixels per second of smooth vertical auto-scroll. 0 disables scrolling (static page).</summary>
    [property: JsonPropertyName("scrollSpeed")] int ScrollSpeed,
    [property: JsonPropertyName("enabled")] bool Enabled)
{
    public const int DefaultRefreshSeconds = 20;
    public const int MinRefreshSeconds = 3;
    public const int DefaultScrollSpeed = 55;

    /// <summary>A fresh file with sensible defaults at the given path and title (columns are shared, so none here).</summary>
    public static MonitorFile New(string path, string title) =>
        new(path, title, GroupNames: [], Columns: null,
            RefreshSeconds: DefaultRefreshSeconds, ScrollSpeed: DefaultScrollSpeed, Enabled: true);
}
