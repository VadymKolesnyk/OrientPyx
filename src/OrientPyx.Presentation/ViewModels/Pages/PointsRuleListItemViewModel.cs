using CommunityToolkit.Mvvm.ComponentModel;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.Localization;

namespace OrientPyx.Presentation.ViewModels.Pages;

/// <summary>
/// One entry in the left-hand list of points rules on the Points ("Очки") page: the rule's name and a
/// short kind label (table vs formula). The full edit happens in the right-hand detail editor.
/// </summary>
public sealed partial class PointsRuleListItemViewModel : ObservableObject
{
    public PointsRuleListItemViewModel(PointsRule rule, ILocalizationService localization)
    {
        Id = rule.Id;
        Kind = rule.Kind;
        Localization = localization;
        _name = rule.Name;
    }

    public Guid Id { get; }

    public PointsRuleKind Kind { get; private set; }

    /// <summary>Copies a freshly-read rule onto this item; only a changed name or kind raises a property change.</summary>
    public void Apply(PointsRule rule)
    {
        if (Kind != rule.Kind)
        {
            Kind = rule.Kind;
            OnPropertyChanged(nameof(Kind));
            OnPropertyChanged(nameof(KindLabel));
        }
        Name = rule.Name;
    }

    public ILocalizationService Localization { get; }

    /// <summary>The rule name as shown in the list; updated live as the detail editor renames the rule.</summary>
    [ObservableProperty]
    private string _name;

    /// <summary>Localized kind label ("Таблиця" / "Формула") for the secondary line.</summary>
    public string KindLabel => Localization.Get(
        Kind == PointsRuleKind.Table ? "Points.Kind.Table" : "Points.Kind.Formula");

    /// <summary>Name shown in the list, falling back to a placeholder while still unnamed.</summary>
    public string DisplayName =>
        string.IsNullOrWhiteSpace(Name) ? Localization.Get("Points.Unnamed") : Name;

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(DisplayName));
}
