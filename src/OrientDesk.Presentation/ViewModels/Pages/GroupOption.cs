using OrientDesk.Localization;

namespace OrientDesk.Presentation.ViewModels.Pages;

/// <summary>
/// A selectable group for a participant's per-day group cell: a group id paired with its display
/// name. A null <see cref="Id"/> is the "(none)" sentinel meaning the participant has no group
/// assigned that day (but is still a member of the day).
/// </summary>
public sealed class GroupOption
{
    private readonly ILocalizationService _localization;

    public GroupOption(Guid? id, string name, ILocalizationService localization)
    {
        Id = id;
        _name = name;
        _localization = localization;
    }

    private readonly string _name;

    /// <summary>Group id, or null for the "(none)" sentinel.</summary>
    public Guid? Id { get; }

    /// <summary>Display name; the localized "(none)" placeholder for the sentinel.</summary>
    public string Label => Id is null ? _localization.Get("Participants.Group.None") : _name;
}
