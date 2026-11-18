using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using OrientPyx.DataAccess.Persistence;

namespace OrientPyx.Presentation.Services;

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

    public bool CollectChipToast
    {
        get => _data.CollectChipToast;
        set
        {
            if (_data.CollectChipToast == value)
                return;
            _data.CollectChipToast = value;
            Write();
        }
    }

    public bool CollectChipSound
    {
        get => _data.CollectChipSound;
        set => Set(_data.CollectChipSound, value, v => _data.CollectChipSound = v);
    }

    public bool UnknownChipSound
    {
        get => _data.UnknownChipSound;
        set => Set(_data.UnknownChipSound, value, v => _data.UnknownChipSound = v);
    }

    public bool CollectChipSystemNotification
    {
        get => _data.CollectChipSystemNotification;
        set => Set(_data.CollectChipSystemNotification, value, v => _data.CollectChipSystemNotification = v);
    }

    public bool UnknownChipSystemNotification
    {
        get => _data.UnknownChipSystemNotification;
        set => Set(_data.UnknownChipSystemNotification, value, v => _data.UnknownChipSystemNotification = v);
    }

    public bool FlashWhenInactive
    {
        get => _data.FlashWhenInactive;
        set => Set(_data.FlashWhenInactive, value, v => _data.FlashWhenInactive = v);
    }

    private void Set(bool current, bool value, Action<bool> assign)
    {
        if (current == value)
            return;
        assign(value);
        Write();
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
        public bool CollectChipToast { get; set; } = true;
        public bool CollectChipSound { get; set; } = true;
        public bool UnknownChipSound { get; set; } = true;
        public bool CollectChipSystemNotification { get; set; } = true;
        public bool UnknownChipSystemNotification { get; set; } = true;
        public bool FlashWhenInactive { get; set; } = true;
    }
}
