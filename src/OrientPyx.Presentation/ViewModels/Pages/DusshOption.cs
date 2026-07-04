using OrientPyx.Localization;

namespace OrientPyx.Presentation.ViewModels.Pages;

/// <summary>
/// A selectable sports school (ДЮСШ) for a participant's school cell: a school id paired with its
/// display name. Three flavours, distinguished by <see cref="Id"/> and <see cref="IsAdd"/>:
/// <list type="bullet">
/// <item>the "(none)" sentinel (<see cref="Id"/> null, <see cref="IsAdd"/> false) — clears the school;</item>
/// <item>a real school (<see cref="Id"/> set) — selecting it assigns that school;</item>
/// <item>the "+ new" sentinel (<see cref="IsAdd"/> true) — opens the create-school modal.</item>
/// </list>
/// Like region/club, ДЮСШ is competition-level, so one shared options list is matched by id across
/// every row (unlike the per-day <see cref="GroupOption"/>, which must be matched by reference).
/// </summary>
public sealed class DusshOption
{
    private readonly ILocalizationService _localization;
    private readonly string _name;

    private DusshOption(Guid? id, string name, bool isAdd, ILocalizationService localization)
    {
        Id = id;
        _name = name;
        IsAdd = isAdd;
        _localization = localization;
    }

    /// <summary>A real sports-school option.</summary>
    public DusshOption(Guid id, string name, ILocalizationService localization)
        : this(id, name, isAdd: false, localization)
    {
    }

    /// <summary>The "(none)" sentinel: clears the participant's school.</summary>
    public static DusshOption None(ILocalizationService localization)
        => new(null, string.Empty, isAdd: false, localization);

    /// <summary>The "+ new" sentinel: selecting it opens the create-school modal.</summary>
    public static DusshOption Add(ILocalizationService localization)
        => new(null, string.Empty, isAdd: true, localization);

    /// <summary>Sports-school id, or null for the "(none)" / "+ new" sentinels.</summary>
    public Guid? Id { get; }

    /// <summary>True for the "+ new" sentinel.</summary>
    public bool IsAdd { get; }

    /// <summary>Display name; the localized "(none)" / "+ new" placeholders for the sentinels.</summary>
    public string Label =>
        IsAdd ? _localization.Get("Participants.Dussh.Add")
        : Id is null ? _localization.Get("Participants.Dussh.None")
        : _name;
}
