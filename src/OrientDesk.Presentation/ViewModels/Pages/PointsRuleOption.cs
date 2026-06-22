using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using OrientDesk.Localization;

namespace OrientDesk.Presentation.ViewModels.Pages;

/// <summary>
/// A selectable points-rule option for a group's per-day points override (ComboBox item). Mirrors
/// <see cref="DisciplineOverrideOption"/>: a null <see cref="Id"/> is the "(default)" sentinel meaning
/// the group inherits the competition-wide default rule; its label names that default when known, e.g.
/// "(default: 45 42 40…)". A non-null id carries the rule's own name. <see cref="None"/> is an explicit
/// "no points" choice (<see cref="Id"/> == <see cref="NoneId"/>): the group is scored without any rule,
/// regardless of the competition default. An id that no longer matches any known rule is shown via a
/// one-off "unknown" option so the row's stored choice is not silently lost. The label re-resolves when
/// the language changes.
/// </summary>
public sealed partial class PointsRuleOption : ObservableObject
{
    /// <summary>
    /// Sentinel <see cref="Id"/> stored for an explicit "no points rule" choice — distinct from null,
    /// which means "inherit the competition default". Kept as <see cref="Guid.Empty"/> so the existing
    /// nullable-Guid column carries it without a schema migration.
    /// </summary>
    public static readonly Guid NoneId = Guid.Empty;

    private readonly ILocalizationService _localization;
    private readonly string? _ruleName;
    private string? _defaultName;
    private readonly bool _unknown;
    private readonly bool _none;

    private PointsRuleOption(
        Guid? id,
        ILocalizationService localization,
        string? ruleName,
        string? defaultName,
        bool unknown,
        bool none)
    {
        Id = id;
        _localization = localization;
        _ruleName = ruleName;
        _defaultName = defaultName;
        _unknown = unknown;
        _none = none;
        _localization.PropertyChanged += OnLocalizationChanged;
    }

    /// <summary>The referenced rule id; null = inherit the competition default, <see cref="NoneId"/> = explicit none.</summary>
    public Guid? Id { get; }

    /// <summary>
    /// Updates the competition default name shown by the "(default: …)" sentinel and refreshes the label.
    /// Only meaningful on a default-sentinel option (no-op visual change otherwise). Lets the Groups page
    /// reflect a changed competition default live without rebuilding every row's combo.
    /// </summary>
    public void UpdateDefaultName(string? defaultName)
    {
        _defaultName = defaultName;
        OnPropertyChanged(nameof(Label));
    }

    /// <summary>The "(default: …)" sentinel; <paramref name="defaultName"/> names the competition default rule.</summary>
    public static PointsRuleOption Default(ILocalizationService localization, string? defaultName)
        => new(null, localization, ruleName: null, defaultName: defaultName, unknown: false, none: false);

    /// <summary>
    /// The plain "немає" option: no points rule. Used in two places with different stored ids:
    /// the competition default combo carries a null id (no default rule set), while a per-group combo
    /// carries <see cref="NoneId"/> (an explicit "score without a rule", distinct from inheriting the
    /// default). The label is the same in both cases.
    /// </summary>
    public static PointsRuleOption None(ILocalizationService localization, bool explicitChoice)
        => new(explicitChoice ? NoneId : null, localization, ruleName: null, defaultName: null, unknown: false, none: true);

    /// <summary>A concrete rule option carrying the rule's name.</summary>
    public static PointsRuleOption ForRule(Guid id, string name, ILocalizationService localization)
        => new(id, localization, ruleName: name, defaultName: null, unknown: false, none: false);

    /// <summary>A one-off option for a stored id that no longer matches a known rule (shown "(unknown)").</summary>
    public static PointsRuleOption Unknown(Guid id, ILocalizationService localization)
        => new(id, localization, ruleName: null, defaultName: null, unknown: true, none: false);

    public string Label
    {
        get
        {
            if (_unknown)
                return _localization.Get("Points.Rule.Unknown");

            // Plain "немає" — no rule (checked before the id branch, since NoneId is non-null).
            if (_none)
                return _localization.Get("Points.Rule.None");

            if (Id is not null)
                return string.IsNullOrWhiteSpace(_ruleName) ? _localization.Get("Points.Unnamed") : _ruleName!;

            // The "(default)" sentinel: name the competition default rule when one is set.
            if (string.IsNullOrWhiteSpace(_defaultName))
                return _localization.Get("Points.Rule.DefaultNone");

            return string.Format(_localization.Get("Points.Rule.DefaultNamed"), _defaultName);
        }
    }

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e)
        => OnPropertyChanged(nameof(Label));
}
