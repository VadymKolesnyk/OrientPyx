using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.Localization;

namespace OrientPyx.Presentation.ViewModels.Pages;

/// <summary>
/// A selectable discipline override with its localized label, used as a ComboBox item for a group's
/// per-day discipline. A null strategy is the "(default)" sentinel meaning the group inherits the day's
/// default discipline; its label also names that default, e.g. "(default: Rogaine)". The label
/// re-resolves when the language changes.
/// </summary>
public sealed partial class DisciplineOverrideOption : ObservableObject
{
    private readonly ILocalizationService _localization;
    private readonly IDisciplineStrategy? _strategy;
    private readonly IDisciplineStrategy? _dayDefault;

    public DisciplineOverrideOption(
        IDisciplineStrategy? strategy,
        ILocalizationService localization,
        IDisciplineStrategy? dayDefault = null)
    {
        _strategy = strategy;
        _dayDefault = dayDefault;
        _localization = localization;
        _localization.PropertyChanged += OnLocalizationChanged;
    }

    public DisciplineId? Value => _strategy?.Id;

    public string Label
    {
        get
        {
            if (_strategy is not null)
                return DisciplineLabels.Of(_strategy, _localization);

            // The "(default)" sentinel: name the day's default discipline when known.
            if (_dayDefault is null)
                return _localization.Get("Groups.Discipline.Default");

            var defaultName = DisciplineLabels.Of(_dayDefault, _localization);
            return string.Format(_localization.Get("Groups.Discipline.DefaultNamed"), defaultName);
        }
    }

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e)
        => OnPropertyChanged(nameof(Label));
}
