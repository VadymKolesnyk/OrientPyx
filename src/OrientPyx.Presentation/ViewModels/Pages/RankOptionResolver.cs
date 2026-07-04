using OrientPyx.Localization;

namespace OrientPyx.Presentation.ViewModels.Pages;

/// <summary>
/// Shared logic for picking a participant's rank option from the application-level rank list. Rank is
/// stored as text, so a participant may hold a value no longer in the list (renamed/deleted). This
/// resolves the value to an option, prepending a one-off "unknown" option (and so a per-row list) when
/// the value is non-blank and unmatched, so the dropdown always shows the stored rank.
/// Used by both participant row view models to keep the matching identical.
/// </summary>
internal static class RankOptionResolver
{
    /// <summary>
    /// Resolves <paramref name="value"/> against <paramref name="sharedOptions"/>. Returns the list the
    /// row's dropdown should use (the shared list, or a per-row copy with an "unknown" option prepended)
    /// and the option to select within it.
    /// </summary>
    public static (IReadOnlyList<RankOption> Options, RankOption Selected) Resolve(
        IReadOnlyList<RankOption> sharedOptions,
        string? value,
        ILocalizationService localization)
    {
        var rank = (value ?? string.Empty).Trim();

        // Blank ⇒ "(none)" (always the first option).
        if (rank.Length == 0)
            return (sharedOptions, sharedOptions[0]);

        var match = sharedOptions.FirstOrDefault(
            o => !o.IsUnknown && string.Equals(o.Value, rank, StringComparison.CurrentCultureIgnoreCase));
        if (match is not null)
            return (sharedOptions, match);

        // Not in the list: prepend a one-off "unknown" option carrying the stored value (after "(none)").
        var unknown = RankOption.Unknown(rank, localization);
        var perRow = new List<RankOption>(sharedOptions.Count + 1) { sharedOptions[0], unknown };
        for (var i = 1; i < sharedOptions.Count; i++)
            perRow.Add(sharedOptions[i]);
        return (perRow, unknown);
    }
}
