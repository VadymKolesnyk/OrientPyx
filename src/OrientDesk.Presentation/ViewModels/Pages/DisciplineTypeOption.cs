using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using OrientDesk.BusinessLogic.Enums;
using OrientDesk.Localization;

namespace OrientDesk.Presentation.ViewModels.Pages;

/// <summary>
/// A selectable discipline (competition type) with its localized label, used as a ComboBox item.
/// The label re-resolves when the interface language changes.
/// </summary>
public sealed partial class DisciplineTypeOption : ObservableObject
{
    private readonly ILocalizationService _localization;

    public DisciplineTypeOption(DisciplineType value, ILocalizationService localization)
    {
        Value = value;
        _localization = localization;
        _localization.PropertyChanged += OnLocalizationChanged;
    }

    public DisciplineType Value { get; }

    public string Label => _localization.Get("Discipline.Type." + Value);

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e)
        => OnPropertyChanged(nameof(Label));
}
