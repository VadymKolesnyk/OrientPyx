using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using OrientDesk.DataAccess.Persistence;

namespace OrientDesk.Presentation.Services;

/// <summary>
/// Default <see cref="IUiPreferencesService"/>: persists app-wide UI preferences to
/// <c>preferences.json</c> in the data folder. Loaded once on construction; each setter writes the whole
/// file back (the file is tiny). Tolerant of a missing/corrupt file and never throws to the caller.
/// </summary>
public sealed class UiPreferencesService : IUiPreferencesService
{
    private const string FileName = "preferences.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _path;
    private PreferencesData _data;

    public UiPreferencesService()
    {
        _path = Path.Combine(AppDatabasePaths.DefaultDataPath, FileName);
        _data = Read(_path);
    }

    public SplitsDock SplitsDock
    {
        get => _data.SplitsDock;
        set
        {
            if (_data.SplitsDock == value)
                return;
            _data.SplitsDock = value;
            Write();
        }
    }

    public double SplitsSize
    {
        get => _data.SplitsSize;
        set
        {
            // Round so a sub-pixel resize delta doesn't churn the file on every drag tick.
            var rounded = Math.Round(value);
            if (Math.Abs(_data.SplitsSize - rounded) < 1)
                return;
            _data.SplitsSize = rounded;
            Write();
        }
    }

    public double SplitsPrescribedWidth
    {
        get => _data.SplitsPrescribedWidth;
        set
        {
            // Round so a sub-pixel resize delta doesn't churn the file on every drag tick.
            var rounded = Math.Round(value);
            if (Math.Abs(_data.SplitsPrescribedWidth - rounded) < 1)
                return;
            _data.SplitsPrescribedWidth = rounded;
            Write();
        }
    }

    private void Write()
    {
        try
        {
            Directory.CreateDirectory(AppDatabasePaths.DefaultDataPath);
            File.WriteAllText(_path, JsonSerializer.Serialize(_data, Options));
        }
        catch
        {
            // A UI preference is convenience state — never crash the UI over a failed write.
        }
    }

    private static PreferencesData Read(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new PreferencesData();
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
                return new PreferencesData();
            return JsonSerializer.Deserialize<PreferencesData>(json, Options) ?? new PreferencesData();
        }
        catch
        {
            return new PreferencesData();
        }
    }

    // The on-disk shape. New preferences are added as properties with a sensible default so an older
    // file (missing the key) still loads.
    private sealed class PreferencesData
    {
        public SplitsDock SplitsDock { get; set; } = SplitsDock.Bottom;
        public double SplitsSize { get; set; } = 200;
        public double SplitsPrescribedWidth { get; set; } = 320;
    }
}
