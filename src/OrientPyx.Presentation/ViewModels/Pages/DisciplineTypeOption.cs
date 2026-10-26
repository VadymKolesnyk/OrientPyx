using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.Localization;

namespace OrientPyx.Presentation.ViewModels.Pages;

/// <summary>
/// A selectable discipline (competition type) with its localized label, used as a ComboBox item.
/// The label re-resolves when the interface language changes.
/// </summary>
public sealed partial class DisciplineTypeOption : ObservableObject
{
    private readonly ILocalizationService _localization;
    private readonly IDisciplineStrategy _strategy;

    public DisciplineTypeOption(IDisciplineStrategy strategy, ILocalizationService localization)
    {
        _strategy = strategy;
        _localization = localization;
        _localization.PropertyChanged += OnLocalizationChanged;
    }

    public DisciplineId Value => _strategy.Id;

    public IDisciplineStrategy Strategy => _strategy;

    public string Label => DisciplineLabels.Of(_strategy, _localization, markPlugin: true);

    /// <summary>The selectable options: every registered discipline, plus <paramref name="current"/> when it is
    /// not registered (a missing plugin) so the stored value still shows instead of snapping to another one.</summary>
    public static IReadOnlyList<DisciplineTypeOption> ListFor(
        IDisciplineStrategyProvider strategies, ILocalizationService localization, DisciplineId current)
    {
        var options = strategies.All.Select(s => new DisciplineTypeOption(s, localization)).ToList();
        if (options.All(o => o.Value != current))
            options.Add(new DisciplineTypeOption(strategies.For(current), localization));
        return options;
    }

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e)
        => OnPropertyChanged(nameof(Label));
}

/// <summary>Localized display name of a discipline, including the "unknown (needs plugin)" placeholder.</summary>
public static class DisciplineLabels
{
    /// <param name="markPlugin">Append «(плагін)» to a plugin discipline — for the type dropdowns.</param>
    public static string Of(IDisciplineStrategy strategy, ILocalizationService localization, bool markPlugin = false)
    {
        if (strategy.IsUnknown)
            return string.Format(localization.Get(strategy.NameKey), strategy.Id.Value);
        var name = localization.Get(strategy.NameKey);
        return markPlugin && strategy.PluginId is not null
            ? $"{name} {localization.Get("Discipline.PluginSuffix")}"
            : name;
    }

    /// <summary>The key of the localized name, for code that only stores a key: a plugin's own string or a
    /// built-in key.</summary>
    public static string KeyOf(IDisciplineStrategy strategy) => strategy.NameKey;
}
