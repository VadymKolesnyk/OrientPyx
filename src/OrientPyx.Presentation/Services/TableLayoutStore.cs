using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.Presentation.Controls;

namespace OrientPyx.Presentation.Services;

/// <summary>
/// Default <see cref="ITableLayoutStore"/>: persists table layouts to <c>views.json</c> in the current
/// competition's folder. The file is one JSON object mapping table id → <see cref="TableLayout"/>, so
/// every table on the competition shares one file. Tolerant of a missing/corrupt file (treated as "no
/// layout"); failures never throw to the caller — a view layout is convenience state, not data.
/// </summary>
public sealed class TableLayoutStore : ITableLayoutStore
{
    private const string FileName = "views.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly ISessionService _session;

    public TableLayoutStore(ISessionService session)
    {
        _session = session;
    }

    public string? CurrentScopeId => _session.CurrentEvent?.FolderPath;

    public TableLayout? Load(string tableKey)
    {
        if (FilePath() is not { } path)
            return null;

        var all = ReadAll(path);
        return all.TryGetValue(tableKey, out var layout) ? layout : null;
    }

    public void Save(string tableKey, TableLayout layout)
    {
        if (FilePath() is not { } path)
            return;

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

    // The views.json path for the current competition, or null when none is selected.
    private string? FilePath()
    {
        var folder = _session.CurrentEvent?.FolderPath;
        return string.IsNullOrEmpty(folder) ? null : Path.Combine(folder, FileName);
    }

    // Reads the whole table id → layout map; an empty map on a missing/corrupt file.
    private static Dictionary<string, TableLayout> ReadAll(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new Dictionary<string, TableLayout>();
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
                return new Dictionary<string, TableLayout>();
            return JsonSerializer.Deserialize<Dictionary<string, TableLayout>>(json, Options)
                   ?? new Dictionary<string, TableLayout>();
        }
        catch
        {
            return new Dictionary<string, TableLayout>();
        }
    }
}
