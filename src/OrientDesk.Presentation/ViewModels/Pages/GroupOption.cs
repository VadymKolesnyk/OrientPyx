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

    public GroupOption(Guid? id, string name, ILocalizationService localization,
        int? minBirthYear = null, int? maxBirthYear = null)
    {
        Id = id;
        _name = name;
        _localization = localization;
        MinBirthYear = minBirthYear;
        MaxBirthYear = maxBirthYear;
    }

    private readonly string _name;

    /// <summary>Group id, or null for the "(none)" sentinel.</summary>
    public Guid? Id { get; }

    /// <summary>Earliest allowed birth year, inclusive ("не старше"); null = no lower bound.</summary>
    public int? MinBirthYear { get; }

    /// <summary>Latest allowed birth year, inclusive ("не молодше"); null = no upper bound.</summary>
    public int? MaxBirthYear { get; }

    /// <summary>Display name; the localized "(none)" placeholder for the sentinel.</summary>
    public string Label => Id is null ? _localization.Get("Participants.Group.None") : _name;

    /// <summary>
    /// A localized explanation of why a participant born in <paramref name="birthYear"/> falls outside this
    /// group's age window — naming the group and the breached bound — or an empty string when there is no
    /// violation (date/group unset, or within the window). Used as the birth-date cell's tooltip.
    /// </summary>
    public string AgeViolationReason(int? birthYear)
    {
        if (birthYear is not { } year)
            return string.Empty;
        if (MinBirthYear is { } min && year < min)
            return string.Format(_localization.Get("Participants.AgeViolation.TooOld"), Label, min);
        if (MaxBirthYear is { } max && year > max)
            return string.Format(_localization.Get("Participants.AgeViolation.TooYoung"), Label, max);
        return string.Empty;
    }
}
