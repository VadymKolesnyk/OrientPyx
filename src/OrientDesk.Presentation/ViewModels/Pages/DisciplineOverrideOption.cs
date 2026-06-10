using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using OrientDesk.BusinessLogic.Enums;
using OrientDesk.Localization;

namespace OrientDesk.Presentation.ViewModels.Pages;

/// <summary>
/// A selectable discipline override with its localized label, used as a ComboBox item for a group's
/// per-day discipline. A null <see cref="Value"/> is the "(default)" sentinel meaning the group
/// inherits the day's default discipline; its label also names that default, e.g.
/// "(default: Rogaine)". The label re-resolves when the language changes.
/// </summary>
public sealed partial class DisciplineOverrideOption : ObservableObject
{
    private readonly ILocalizationService _localization;
    private readonly DisciplineType? _dayDefault;

    public DisciplineOverrideOption(
        DisciplineType? value,
        ILocalizationService localization,
        DisciplineType? dayDefault = null)
    {
        Value = value;
        _dayDefault = dayDefault;
        _localization = localization;
        _localization.PropertyChanged += OnLocalizationChanged;
    }

    public DisciplineType? Value { get; }

    public string Label
    {
        get
        {
            if (Value is not null)
                return _localization.Get("Discipline.Type." + Value);

            // The "(default)" sentinel: name the day's default discipline when known.
            if (_dayDefault is null)
                return _localization.Get("Groups.Discipline.Default");

            var defaultName = _localization.Get("Discipline.Type." + _dayDefault);
            return string.Format(_localization.Get("Groups.Discipline.DefaultNamed"), defaultName);
        }
    }

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e)
        => OnPropertyChanged(nameof(Label));
}
