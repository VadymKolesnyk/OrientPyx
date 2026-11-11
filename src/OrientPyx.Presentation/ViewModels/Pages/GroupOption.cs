using OrientPyx.Localization;

namespace OrientPyx.Presentation.ViewModels.Pages;

/// <summary>
/// A selectable group for a participant's per-day group cell: a group id paired with its display
/// name. A null <see cref="Id"/> is the "(none)" sentinel meaning the participant has no group
/// assigned that day (but is still a member of the day).
/// </summary>
public sealed class GroupOption
{
    private readonly ILocalizationService _localization;

    public GroupOption(Guid? id, string name, ILocalizationService localization,
        int? minBirthYear = null, int? maxBirthYear = null, int controlCount = 0, bool isRelay = false)
    {
        Id = id;
        _name = name;
        _localization = localization;
        MinBirthYear = minBirthYear;
        MaxBirthYear = maxBirthYear;
        ControlCount = controlCount;
        IsRelay = isRelay;
    }

    /// <summary>The group runs a relay on the day — a team runner's whole team may be moved into it.</summary>
    public bool IsRelay { get; }

    /// <summary>
    /// The most control punches a member of this group may make on the day (<see cref="ChipCapacity.NeededPunches"/>);
    /// 0 when the group has no course yet. A chip whose model holds fewer punches is flagged on the chip cell.
    /// </summary>
    public int ControlCount { get; }

    /// <summary>
    /// A localized explanation that <paramref name="chip"/> is a SportIdent card too small for this group's course
    /// (names the model, its capacity, the group and its control count), or an empty string when the chip fits,
    /// is unknown or blank, or the group has no course. Used as the chip cell's tooltip.
    /// </summary>
    public string ChipCapacityReason(string? chip)
    {
        if (SiCard.TooSmall(chip, ControlCount) is not { } card)
            return string.Empty;
        return string.Format(_localization.Get("Participants.ChipCapacity.TooSmall"),
            (chip ?? string.Empty).Trim(), card.Model, card.Capacity, Label, ControlCount);
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
