using OrientPyx.Localization;

namespace OrientPyx.Presentation.ViewModels.Pages;

/// <summary>
/// The selected participant row as the table's status bar shows it on the left: «15 / 100.2 — Іваненко Іван»
/// (the bib and / or the relay leg number, whichever the row has, then the name).
/// </summary>
internal static class ParticipantSelectionCaption
{
    public static string Format(string? number, string? legBib, string? fullName, ILocalizationService localization)
    {
        var numbers = string.Join(" / ", new[] { number?.Trim(), legBib?.Trim() }.Where(s => !string.IsNullOrEmpty(s)));
        var name = fullName?.Trim() ?? string.Empty;
        if (numbers.Length == 0)
            return name;
        if (name.Length == 0)
            return numbers;
        return string.Format(localization.Get("Participants.Status.SelectedRow"), numbers, name);
    }
}
