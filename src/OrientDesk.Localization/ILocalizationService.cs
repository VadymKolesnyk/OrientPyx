using System.ComponentModel;
using System.Globalization;

namespace OrientDesk.Localization;

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

    /// <summary>Switches the active language. No-op if the culture is already active or unavailable.</summary>
    void SetLanguage(CultureInfo culture);
}
