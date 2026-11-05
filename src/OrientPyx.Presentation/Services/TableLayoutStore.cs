using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.DataAccess.Persistence;
using OrientPyx.Presentation.Controls;

namespace OrientPyx.Presentation.Services;

/// <summary>
/// Default <see cref="ITableLayoutStore"/>. Two JSON files, same shape (one object mapping table id →
/// <see cref="TableLayout"/>): the current competition's <c>views.json</c>, and the application-wide
/// <c>views-default.json</c> in the data root. Tolerant of a missing/corrupt/implausible file (treated as
/// "no layout"); failures never throw to the caller — a view layout is convenience state, not data.
/// </summary>
public sealed class TableLayoutStore : ITableLayoutStore
{
    private const string FileName = "views.json";
    private const string DefaultsFileName = "views-default.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly ISessionService _session;
    private readonly IWorkspaceMode _mode;

    public TableLayoutStore(ISessionService session, IWorkspaceMode mode)
    {
        _session = session;
        _mode = mode;
    }

    // Locally the competition folder; on a station "<hostId>/<identifier>" — the view is the operator's own, kept
    // on this machine per host competition, never on the host.
    public string? CurrentScopeId =>
        _session.CurrentEvent is not { } ev ? null
        : _mode.Role == WorkspaceRole.Station && _mode.Station is { } station ? $"{station.HostId}/{ev.Identifier}"
        : ev.FolderPath;

    public TableLayout? Load(string tableKey)
    {
        // The competition's own view wins; a competition that has never been arranged falls back to the
        // application default the user saved from some other competition.
        if (FilePath() is { } path && ReadAll(path).TryGetValue(tableKey, out var own))
            return own;

        return ReadAll(DefaultsPath()).TryGetValue(tableKey, out var fallback) ? fallback : null;
    }

    public void Save(string tableKey, TableLayout layout)
    {
        if (FilePath() is not { } path)
            return;
        Write(path, tableKey, layout);
    }

    public void SaveDefault(string tableKey, TableLayout layout)
        => Write(DefaultsPath(), tableKey, layout);

    public void ClearLayout(string tableKey)
    {
        if (FilePath() is { } path)
            Remove(path, tableKey);
        Remove(DefaultsPath(), tableKey);
    }

    public bool HasDefault(string tableKey) => ReadAll(DefaultsPath()).ContainsKey(tableKey);

    // The views.json path for the current competition, or null when none is selected. On a station the file lives
    // under <data root>/views/<hostId>/<identifier>.json (the competition folder is on the host).
    private string? FilePath()
    {
        if (_session.CurrentEvent is not { } ev)
            return null;
        if (_mode.Role == WorkspaceRole.Station && _mode.Station is { } station)
        {
            var folder = Path.Combine(AppDatabasePaths.BaseDirectory, "views", Safe(station.HostId));
            try
            {
                Directory.CreateDirectory(folder);
            }
            catch
            {
                return null;
            }
            return Path.Combine(folder, Safe(ev.Identifier) + ".json");
        }
        return string.IsNullOrEmpty(ev.FolderPath) ? null : Path.Combine(ev.FolderPath, FileName);
    }

    private static string Safe(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return chars.Length == 0 ? "_" : new string(chars);
    }

    // The application-wide defaults file, next to data/ and events/ in the (possibly redirected) data root.
    private static string DefaultsPath()
        => Path.Combine(AppDatabasePaths.BaseDirectory, DefaultsFileName);

    private static void Write(string path, string tableKey, TableLayout layout)
    {
        try
        {
            var all = ReadAll(path);
            all[tableKey] = layout;
            File.WriteAllText(path, JsonSerializer.Serialize(all, Options));
        }
        catch
        {
            // A view layout is convenience state — never crash the UI over a failed write.
        }
    }

    private static void Remove(string path, string tableKey)
    {
        try
        {
            var all = ReadAll(path);
            if (all.Remove(tableKey))
                File.WriteAllText(path, JsonSerializer.Serialize(all, Options));
        }
        catch
        {
        }
    }

    // Reads the whole table id → layout map; an empty map on a missing/corrupt file. Entries that survive
    // parsing are still sanitised (see Sanitise) — the files are hand-editable and carried between
    // installations, so a plausible-looking but nonsensical entry must not reach the table.
    private static Dictionary<string, TableLayout> ReadAll(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new Dictionary<string, TableLayout>();
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
                return new Dictionary<string, TableLayout>();
            var all = JsonSerializer.Deserialize<Dictionary<string, TableLayout>>(json, Options);
            if (all is null)
                return new Dictionary<string, TableLayout>();

            var clean = new Dictionary<string, TableLayout>(all.Count);
            foreach (var (key, layout) in all)
                if (!string.IsNullOrWhiteSpace(key) && Sanitise(layout) is { } sane)
                    clean[key] = sane;
            return clean;
        }
        catch
        {
            return new Dictionary<string, TableLayout>();
        }
    }

    // Drops anything the table could not act on: a null entry, blank keys/signatures, duplicates, and
    // widths that are not a usable pixel size. Returns null when the entry carries nothing at all, so a
    // junk record reads as "no saved layout" and the table keeps its build defaults. Unknown keys are NOT
    // an error — they belong to columns this build no longer has, and the table already ignores them.
    private static TableLayout? Sanitise(TableLayout? layout)
    {
        if (layout is null)
            return null;

        var result = new TableLayout();

        var seenOrder = new HashSet<string>();
        foreach (var signature in layout.Order ?? [])
            if (!string.IsNullOrWhiteSpace(signature) && seenOrder.Add(signature))
                result.Order.Add(signature);

        var seenHidden = new HashSet<string>();
        foreach (var key in layout.Hidden ?? [])
            if (!string.IsNullOrWhiteSpace(key) && seenHidden.Add(key))
                result.Hidden.Add(key);

        foreach (var (key, column) in layout.Columns ?? [])
        {
            if (string.IsNullOrWhiteSpace(key) || column is null)
                continue;
            // A NaN/infinite/absurd width would make the header measure to nothing or push the table into
            // an unusable horizontal scroll; treat it as "no saved width" and keep the built default.
            var width = column.Width is { } w && double.IsFinite(w) && w is >= MinWidth and <= MaxWidth
                ? w
                : (double?)null;
            if (width is not null)
                result.Columns[key] = new ColumnLayout { Width = width };
        }

        return result.Order.Count == 0 && result.Hidden.Count == 0 && result.Columns.Count == 0
            ? null
            : result;
    }

    // Bounds a persisted column width must fall in to be usable: narrower than this is unclickable, wider
    // is a runaway drag that would strand the rest of the columns off-screen.
    private const double MinWidth = 16;
    private const double MaxWidth = 2000;
}
