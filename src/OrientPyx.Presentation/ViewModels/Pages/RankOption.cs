using OrientPyx.Localization;

namespace OrientPyx.Presentation.ViewModels.Pages;

/// <summary>
/// A selectable rank for a participant's rank cell. Unlike region/club, a rank is stored on the
/// participant as <b>text</b> (its name), so an option carries the rank name itself rather than an id.
/// Three flavours, distinguished by <see cref="Value"/>:
/// <list type="bullet">
/// <item>the "(none)" sentinel (<see cref="Value"/> empty) — clears the participant's rank;</item>
/// <item>a known rank (<see cref="Value"/> = a seeded/added rank name);</item>
/// <item>an "unknown" option (<see cref="IsUnknown"/> true) — a value a participant already holds that
/// is no longer in the rank list (old/renamed/deleted), preserved so the dropdown can show it as-is.</item>
/// </list>
/// Options are matched by their (case-insensitive) value, so one shared list serves every row.
/// </summary>
public sealed class RankOption
{
    private readonly ILocalizationService _localization;

    private RankOption(string value, bool isUnknown, ILocalizationService localization)
    {
        Value = value;
        IsUnknown = isUnknown;
        _localization = localization;
    }

    /// <summary>A known rank option.</summary>
    public RankOption(string name, ILocalizationService localization)
        : this(name, isUnknown: false, localization)
    {
    }

    /// <summary>The "(none)" sentinel: clears the participant's rank.</summary>
    public static RankOption None(ILocalizationService localization)
        => new(string.Empty, isUnknown: false, localization);

    /// <summary>An option for a value not in the rank list, so a participant's old rank still shows.</summary>
    public static RankOption Unknown(string value, ILocalizationService localization)
        => new(value, isUnknown: true, localization);

    /// <summary>The rank name stored on the participant; empty for "(none)".</summary>
    public string Value { get; }

    /// <summary>True when this is a stray value not present in the current rank list.</summary>
    public bool IsUnknown { get; }

    /// <summary>Display text: the localized "(none)" placeholder for the sentinel, else the rank name.</summary>
    public string Label =>
        Value.Length == 0 ? _localization.Get("Participants.Rank.None") : Value;
}
