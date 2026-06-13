using OrientDesk.Localization;

namespace OrientDesk.Presentation.ViewModels.Pages;

/// <summary>
/// A selectable region for a participant's region cell: a region id paired with its display name.
/// Three flavours, distinguished by <see cref="Id"/> and <see cref="IsAdd"/>:
/// <list type="bullet">
/// <item>the "(none)" sentinel (<see cref="Id"/> null, <see cref="IsAdd"/> false) — clears the region;</item>
/// <item>a real region (<see cref="Id"/> set) — selecting it assigns that region;</item>
/// <item>the "+ new" sentinel (<see cref="IsAdd"/> true) — opens the create-region modal.</item>
/// </list>
/// Region is competition-level, so one shared options list is matched by id across every row (unlike
/// the per-day <see cref="GroupOption"/>, which must be matched by reference).
/// </summary>
public sealed class RegionOption
{
    private readonly ILocalizationService _localization;
    private readonly string _name;

    private RegionOption(Guid? id, string name, bool isAdd, ILocalizationService localization)
    {
        Id = id;
        _name = name;
        IsAdd = isAdd;
        _localization = localization;
    }

    /// <summary>A real region option.</summary>
    public RegionOption(Guid id, string name, ILocalizationService localization)
        : this(id, name, isAdd: false, localization)
    {
    }

    /// <summary>The "(none)" sentinel: clears the participant's region.</summary>
    public static RegionOption None(ILocalizationService localization)
        => new(null, string.Empty, isAdd: false, localization);

    /// <summary>The "+ new" sentinel: selecting it opens the create-region modal.</summary>
    public static RegionOption Add(ILocalizationService localization)
        => new(null, string.Empty, isAdd: true, localization);

    /// <summary>Region id, or null for the "(none)" / "+ new" sentinels.</summary>
    public Guid? Id { get; }

    /// <summary>True for the "+ new" sentinel.</summary>
    public bool IsAdd { get; }

    /// <summary>Display name; the localized "(none)" / "+ new" placeholders for the sentinels.</summary>
    public string Label =>
        IsAdd ? _localization.Get("Participants.Region.Add")
        : Id is null ? _localization.Get("Participants.Region.None")
        : _name;
}
