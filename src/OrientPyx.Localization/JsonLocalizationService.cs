using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace OrientPyx.Localization;

/// <summary>
/// Loads localization dictionaries from embedded JSON resources (Resources/{culture}.json)
/// and serves them by key. Supports runtime language switching.
/// </summary>
public sealed class JsonLocalizationService : ILocalizationService
{
    private const string DefaultCultureName = "uk-UA";
    private const string ResourceFolder = "Resources";

    private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _byCulture = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<string, string> _current = new Dictionary<string, string>();

    // Plugin strings: culture → full key ("<prefix>.<key>") → text, plus the registered prefixes.
    private readonly Dictionary<string, Dictionary<string, string>> _overlays = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _overlayPrefixes = new(StringComparer.Ordinal);
    private const string FallbackCultureName = "en-US";

    public JsonLocalizationService()
    {
        LoadAllCultures();

        var defaultCulture = AvailableCultures.FirstOrDefault(c => string.Equals(c.Name, DefaultCultureName, StringComparison.OrdinalIgnoreCase))
            ?? AvailableCultures.FirstOrDefault()
            ?? CultureInfo.GetCultureInfo(DefaultCultureName);

        CurrentCulture = defaultCulture;
        _byCulture.TryGetValue(CurrentCulture.Name, out var dict);
        _current = dict ?? new Dictionary<string, string>();

        // Align the thread/app culture with the active language so culture-driven controls
        // (e.g. the calendar's month names and first-day-of-week) match the UI from the start.
        ApplyThreadCulture(CurrentCulture);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public CultureInfo CurrentCulture { get; private set; }

    public IReadOnlyList<CultureInfo> AvailableCultures { get; private set; } = [];

    public string this[string key] => Get(key);

    public string Get(string key)
    {
        if (string.IsNullOrEmpty(key))
            return string.Empty;

        if (_current.TryGetValue(key, out var value))
            return value;
        return _overlayPrefixes.Count > 0 ? GetOverlay(key) : key;
    }

    public void AddOverlay(string culture, string prefix, IReadOnlyDictionary<string, string> strings)
    {
        if (string.IsNullOrWhiteSpace(prefix))
            return;

        _overlayPrefixes.Add(prefix);
        if (!_overlays.TryGetValue(culture, out var dict))
            _overlays[culture] = dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, text) in strings)
            dict[prefix + "." + key] = text;

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    // A plugin key: current culture, else en-US, else the text after the prefix (a plain-text label).
    private string GetOverlay(string key)
    {
        if (_overlays.TryGetValue(CurrentCulture.Name, out var current) && current.TryGetValue(key, out var text))
            return text;
        if (_overlays.TryGetValue(FallbackCultureName, out var fallback) && fallback.TryGetValue(key, out text))
            return text;

        var dot = key.IndexOf('.');
        while (dot > 0)
        {
            if (_overlayPrefixes.Contains(key[..dot]))
                return key[(dot + 1)..];
            dot = key.IndexOf('.', dot + 1);
        }
        return key;
    }

    public void SetLanguage(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);

        if (string.Equals(culture.Name, CurrentCulture.Name, StringComparison.OrdinalIgnoreCase))
            return;

        if (!_byCulture.TryGetValue(culture.Name, out var dict))
            return;

        CurrentCulture = culture;
        _current = dict;
        ApplyThreadCulture(culture);

        // Empty/null property name refreshes every binding, including the indexer.
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    /// <summary>
    /// Pushes <paramref name="culture"/> onto the current and default thread cultures so that
    /// culture-aware controls (the calendar date picker, number/date formatting) follow the
    /// selected UI language without a restart. Newly created controls read these immediately;
    /// an already-open calendar dropdown refreshes the next time it is opened.
    /// </summary>
    private static void ApplyThreadCulture(CultureInfo culture)
    {
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }

    private void LoadAllCultures()
    {
        var assembly = typeof(JsonLocalizationService).Assembly;
        var prefix = $"{assembly.GetName().Name}.{ResourceFolder}.";
        var cultures = new List<CultureInfo>();

        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.StartsWith(prefix, StringComparison.Ordinal) ||
                !resourceName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var cultureName = resourceName[prefix.Length..^".json".Length];
            var dictionary = ReadDictionary(assembly, resourceName);
            if (dictionary is null)
                continue;

            _byCulture[cultureName] = dictionary;
            cultures.Add(CultureInfo.GetCultureInfo(cultureName));
        }

        AvailableCultures = cultures;
    }

    private static IReadOnlyDictionary<string, string>? ReadDictionary(Assembly assembly, string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
            return null;

        var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
        return parsed ?? new Dictionary<string, string>();
    }
}
