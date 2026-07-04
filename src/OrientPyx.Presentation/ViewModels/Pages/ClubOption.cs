using OrientPyx.Localization;

namespace OrientPyx.Presentation.ViewModels.Pages;

/// <summary>
/// A selectable club for a participant's club cell. Same three flavours as <see cref="RegionOption"/>:
/// the "(none)" sentinel (clears the club), a real club, and the "+ new" sentinel (opens the
/// create-club modal). Club is competition-level, so one shared list is matched by id across rows.
/// </summary>
public sealed class ClubOption
{
    private readonly ILocalizationService _localization;
    private readonly string _name;

    private ClubOption(Guid? id, string name, bool isAdd, ILocalizationService localization)
    {
        Id = id;
        _name = name;
        IsAdd = isAdd;
        _localization = localization;
    }

    /// <summary>A real club option.</summary>
    public ClubOption(Guid id, string name, ILocalizationService localization)
        : this(id, name, isAdd: false, localization)
    {
    }

    /// <summary>The "(none)" sentinel: clears the participant's club.</summary>
    public static ClubOption None(ILocalizationService localization)
        => new(null, string.Empty, isAdd: false, localization);

    /// <summary>The "+ new" sentinel: selecting it opens the create-club modal.</summary>
    public static ClubOption Add(ILocalizationService localization)
        => new(null, string.Empty, isAdd: true, localization);

    /// <summary>Club id, or null for the "(none)" / "+ new" sentinels.</summary>
    public Guid? Id { get; }

    /// <summary>True for the "+ new" sentinel.</summary>
    public bool IsAdd { get; }

    /// <summary>Display name; the localized "(none)" / "+ new" placeholders for the sentinels.</summary>
    public string Label =>
        IsAdd ? _localization.Get("Participants.Club.Add")
        : Id is null ? _localization.Get("Participants.Club.None")
        : _name;
}
