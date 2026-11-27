namespace OrientPyx.BusinessLogic.Models;

/// <summary>
/// A row of a current-day-only import that would create a brand-new participant although someone with the
/// same full name already ran in the same group on another day (or is already on the target day). Most often
/// it is the same person whose FOU code was blank or different in one of the files, so the user is asked
/// whether to merge the row into one of <see cref="Candidates"/> or really add a new athlete.
/// </summary>
/// <param name="RowIndex">Index of the row in <see cref="UofParticipantData.Participants"/>.</param>
public sealed record ImportDuplicateCase(
    int RowIndex,
    ImportDuplicateRow Imported,
    IReadOnlyList<ImportDuplicateCandidate> Candidates);

/// <summary>The imported row's identifying details, as shown next to the existing candidates.</summary>
public sealed record ImportDuplicateRow(
    string FullName,
    string Group,
    int? BirthYear,
    string Club,
    string Region,
    string FsouCode);

/// <summary>An existing participant the imported row may be the same person as.</summary>
/// <param name="DayNumbers">Days on which this participant ran in the imported row's group.</param>
public sealed record ImportDuplicateCandidate(
    Guid ParticipantId,
    string FullName,
    int? BirthYear,
    string Club,
    string Region,
    string FsouCode,
    IReadOnlyList<int> DayNumbers)
{
    /// <summary>
    /// Whether the details that are known on both sides don't contradict the imported row (birth year and FOU
    /// code): a likely "same person". Used only to pre-select the choice — the user always decides.
    /// </summary>
    public bool LooksLikeSamePerson(ImportDuplicateRow row) =>
        (BirthYear is null || row.BirthYear is null || BirthYear == row.BirthYear)
        && (FsouCode.Length == 0 || row.FsouCode.Length == 0
            || TextKey.Same(FsouCode, row.FsouCode));
}

/// <summary>Shared full-name key used to match athletes by «Прізвище Ім'я».</summary>
public static class ParticipantNameKey
{
    /// <summary>The name's <see cref="TextKey"/> (whitespace-normalised, case- and look-alike-letter-folded).</summary>
    public static string Of(string? name) => TextKey.Of(name);
}

/// <summary>
/// Which of several same-name athletes a file row linked by full name belongs to (a day-scoped import with
/// «ПІБ» as the link field). One namesake is that one; among several the row's birth date decides, then its birth year,
/// then its club; with nothing to tell them apart the first stays the match, as before. The store's import and the
/// duplicate check both call it, so they agree.
/// </summary>
public static class ImportNameMatch
{
    public static T? Pick<T>(IReadOnlyList<T> namesakes, DateTimeOffset? birthDate, Guid? clubId,
        Func<T, DateTimeOffset?> birthOf, Func<T, Guid?> clubOf) where T : class
    {
        if (namesakes.Count <= 1)
            return namesakes.Count == 1 ? namesakes[0] : null;
        if (birthDate is { } born)
        {
            var sameDate = namesakes.Where(p => birthOf(p)?.Date == born.Date).ToList();
            if (sameDate.Count == 1)
                return sameDate[0];
            var sameYear = namesakes.Where(p => birthOf(p)?.Year == born.Year).ToList();
            if (sameYear.Count == 1)
                return sameYear[0];
        }
        if (clubId is { } club)
        {
            var sameClub = namesakes.Where(p => clubOf(p) == club).ToList();
            if (sameClub.Count == 1)
                return sameClub[0];
        }
        return namesakes[0];
    }

    /// <summary>True when the row matched one of several namesakes only by being the first (nothing told them apart).</summary>
    public static bool IsGuess<T>(IReadOnlyList<T> namesakes, DateTimeOffset? birthDate, Guid? clubId,
        Func<T, DateTimeOffset?> birthOf, Func<T, Guid?> clubOf) where T : class
    {
        if (namesakes.Count <= 1)
            return false;
        if (birthDate is { } born && (namesakes.Count(p => birthOf(p)?.Date == born.Date) == 1 || namesakes.Count(p => birthOf(p)?.Year == born.Year) == 1))
            return false;
        return clubId is not { } club || namesakes.Count(p => clubOf(p) == club) != 1;
    }
}
