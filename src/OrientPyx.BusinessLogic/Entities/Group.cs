namespace OrientPyx.BusinessLogic.Entities;

/// <summary>
/// A competition-level group (e.g. "М21", "Ж14", "Відкрита"). Groups belong to the whole
/// competition, not a single day; a group's presence on a given day is expressed by a
/// <see cref="GroupDaySettings"/> row referencing it.
/// </summary>
public class Group
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Display name; unique per competition (case-insensitive).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Base start-entry fee for this group, shared across every day the group runs (entry fees are a
    /// group-level, not per-day, concern). Null = unset. Edited on the «Стартові внески» page and summed
    /// into each member's total entry fee on the participants table.
    /// </summary>
    public decimal? EntryFee { get; set; }

    /// <summary>
    /// Earliest birth year a member may have, inclusive ("не старше" — born this year or later). Null = no lower
    /// bound. A participant born before this year falls outside the group's age window. Group-level (shared
    /// across days), but editable from any day's groups grid.
    /// </summary>
    public int? MinBirthYear { get; set; }

    /// <summary>
    /// Latest birth year a member may have, inclusive ("не молодше" — born this year or earlier). Null = no upper
    /// bound. A participant born after this year falls outside the group's age window. Group-level, like
    /// <see cref="MinBirthYear"/>.
    /// </summary>
    public int? MaxBirthYear { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>
    /// Derives a default age window from a standard orienteering group name and the competition's start year.
    /// Names of the form &lt;letter&gt;&lt;number&gt; — where the letter is one of Ж/Ч/М/W/M (so "М16", "Ж14",
    /// "W21", "M35") — encode an age class by the trailing number:
    /// <list type="bullet">
    /// <item>21 → no restriction (the open/elite class).</item>
    /// <item>below 21 (a youth class, e.g. 16) → members must be <b>younger</b> than that age, i.e. born in
    /// <c>startYear - age</c> or later → <see cref="MaxBirthYear"/> = null, <see cref="MinBirthYear"/> set.</item>
    /// <item>above 21 (a veteran class, e.g. 35) → members must be <b>older</b> than that age, i.e. born in
    /// <c>startYear - age</c> or earlier → <see cref="MinBirthYear"/> = null, <see cref="MaxBirthYear"/> set.</item>
    /// </list>
    /// Any other name (no recognised letter, no number, "Відкрита", etc.) gets no restriction (both null).
    /// Returns the computed (min, max) bounds; applied at group creation only — never overwrites an
    /// existing window.
    /// </summary>
    public static (int? MinBirthYear, int? MaxBirthYear) DeriveAgeWindow(string? name, int startYear)
    {
        if (string.IsNullOrWhiteSpace(name))
            return (null, null);

        var trimmed = name.Trim();
        if (trimmed.Length < 2)
            return (null, null);

        var letter = char.ToUpperInvariant(trimmed[0]);
        if (letter is not ('Ж' or 'Ч' or 'М' or 'W' or 'M'))
            return (null, null);

        // Read the leading run of digits right after the class letter; stop at the first non-digit so
        // suffixes like "М21Е" or "Ж35B" still parse their number.
        var digits = 0;
        var any = false;
        for (var i = 1; i < trimmed.Length; i++)
        {
            var c = trimmed[i];
            if (c is < '0' or > '9')
                break;
            digits = digits * 10 + (c - '0');
            any = true;
        }

        if (!any)
            return (null, null);

        if (digits == 21)
            return (null, null);

        // Birth year of someone who turns `digits` during the competition year.
        var boundary = startYear - digits;
        return digits < 21
            ? (boundary, null)   // youth: born this year or later (younger than the age)
            : (null, boundary);  // veteran: born this year or earlier (older than the age)
    }

    /// <summary>
    /// True when a participant born in <paramref name="birthYear"/> falls OUTSIDE the age window
    /// [<paramref name="minBirthYear"/>, <paramref name="maxBirthYear"/>] (both inclusive, either bound
    /// optional). A null <paramref name="birthYear"/> never violates (an unknown birth date can't be
    /// judged). With no bounds set, nothing violates. Shared rule for the participant-table highlight.
    /// </summary>
    public static bool ViolatesAgeWindow(int? birthYear, int? minBirthYear, int? maxBirthYear)
    {
        if (birthYear is not { } year)
            return false;
        if (minBirthYear is { } min && year < min)
            return true;
        if (maxBirthYear is { } max && year > max)
            return true;
        return false;
    }
}
