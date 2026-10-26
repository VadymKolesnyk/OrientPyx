using System.ComponentModel;
using System.Globalization;

namespace OrientPyx.Localization;

/// <summary>
/// Provides localized strings by key and supports switching the active language at runtime.
/// Views can bind to the indexer; raising <see cref="INotifyPropertyChanged.PropertyChanged"/>
/// for the indexer ("Item[]") refreshes all bindings without restarting the app.
/// </summary>
public interface ILocalizationService : INotifyPropertyChanged
{
    /// <summary>The currently active culture (e.g. uk-UA).</summary>
    CultureInfo CurrentCulture { get; }

    /// <summary>Cultures that have loaded resources.</summary>
    IReadOnlyList<CultureInfo> AvailableCultures { get; }

    /// <summary>Returns the localized string for <paramref name="key"/>, or the key itself if missing.</summary>
    string this[string key] { get; }

    /// <summary>Returns the localized string for <paramref name="key"/>, or the key itself if missing.</summary>
    string Get(string key);

    /// <summary>
    /// Registers strings contributed from outside the built-in resources (a plugin), under
    /// <c>"&lt;prefix&gt;.&lt;key&gt;"</c>. A prefixed key with no translation in the current culture falls back to
    /// en-US, then to the bare key after the prefix — so a plugin may use plain text instead of a key.
    /// </summary>
    void AddOverlay(string culture, string prefix, IReadOnlyDictionary<string, string> strings);

    /// <summary>Switches the active language. No-op if the culture is already active or unavailable.</summary>
    void SetLanguage(CultureInfo culture);
}
