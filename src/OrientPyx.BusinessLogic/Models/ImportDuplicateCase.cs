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
            || string.Equals(FsouCode, row.FsouCode, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Shared full-name key used to match athletes by «Прізвище Ім'я».</summary>
public static class ParticipantNameKey
{
    /// <summary>Whitespace-normalised full name; compare case-insensitively.</summary>
    public static string Of(string? name) => string.Join(' ',
        (name ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
