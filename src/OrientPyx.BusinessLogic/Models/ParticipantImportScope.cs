namespace OrientPyx.BusinessLogic.Models;

/// <summary>
/// How a participant import maps rows to days and matches existing athletes. The default,
/// <see cref="AllDays"/>, keeps the historical behaviour: each athlete is placed on the day numbers
/// the file lists (or every day when it lists none) and existing athletes are matched by FOU code.
///
/// <see cref="CurrentDayOnly"/> ignores the file's day info entirely and enters every imported
/// athlete on the single day passed as <see cref="TargetDayNumber"/> (the active session day). Because
/// the same person may already exist from another day's import, matching there uses the caller-chosen
/// <see cref="LinkField"/> rather than always FOU code, so the new day link attaches to the existing
/// participant instead of creating a duplicate.
/// </summary>
public sealed class ParticipantImportScope
{
    /// <summary>Import onto all days per the file (legacy behaviour, FOU-code match).</summary>
    public static ParticipantImportScope AllDays { get; } = new()
    {
        Mode = ParticipantImportMode.AllDays,
        LinkField = ParticipantLinkField.FsouCode
    };

    /// <summary>
    /// Import onto only <paramref name="dayNumber"/>, matching by <paramref name="linkField"/>, and — for
    /// a matched existing athlete — overwriting only the participant-level fields in
    /// <paramref name="updateFields"/> (their other days' data must not be disturbed by a day-scoped import,
    /// so by default we touch nothing but the day link; <see cref="ParticipantUpdateFields.Payment"/> is the
    /// caller's usual default). A brand-new athlete is always fully populated regardless.
    /// </summary>
    public static ParticipantImportScope CurrentDay(
        int dayNumber,
        ParticipantLinkField linkField,
        ParticipantUpdateFields updateFields = ParticipantUpdateFields.None) => new()
    {
        Mode = ParticipantImportMode.CurrentDayOnly,
        TargetDayNumber = dayNumber,
        LinkField = linkField,
        UpdateFields = updateFields
    };

    public ParticipantImportMode Mode { get; init; }

    /// <summary>The 1-based day number to import onto in <see cref="ParticipantImportMode.CurrentDayOnly"/>.</summary>
    public int TargetDayNumber { get; init; }

    /// <summary>Which field identifies an already-imported athlete when matching across days.</summary>
    public ParticipantLinkField LinkField { get; init; }

    /// <summary>
    /// In <see cref="ParticipantImportMode.CurrentDayOnly"/>, which participant-level fields of an already
    /// existing (matched) athlete the import may overwrite. Ignored in <see cref="ParticipantImportMode.AllDays"/>,
    /// which keeps its legacy behaviour of overwriting every field.
    /// </summary>
    public ParticipantUpdateFields UpdateFields { get; init; } = ParticipantUpdateFields.None;

    /// <summary>
    /// In <see cref="ParticipantImportMode.CurrentDayOnly"/>, rows the user chose to merge into an existing
    /// participant after the duplicate check (row index in the import data → participant id). Such a row is
    /// treated as matched to that participant even though the link field didn't match it.
    /// </summary>
    public IReadOnlyDictionary<int, Guid> MergeInto { get; init; } = new Dictionary<int, Guid>();

    /// <summary>
    /// Rows of the import data (by index) the user chose to leave out in the all-days preview
    /// (<see cref="ImportPreview"/>): they are neither added nor updated. Empty by default.
    /// </summary>
    public IReadOnlySet<int> ExcludedRows { get; init; } = new HashSet<int>();

    /// <summary>
    /// In <see cref="ParticipantImportMode.AllDays"/>, whether an already existing (matched) athlete's «Оплата»
    /// is taken from the file (the preview's «оновлювати оплату» checkbox). Off, the payment entered at the desk
    /// stays as it is, on the participant and on every day link; a brand-new athlete still gets the file's
    /// value. Default on (the legacy behaviour). Ignored in current-day-only mode, which has
    /// <see cref="ParticipantUpdateFields.Payment"/> for the same decision.
    /// </summary>
    public bool UpdatePayment { get; init; } = true;

    /// <summary>A copy of this scope with <see cref="MergeInto"/> replaced.</summary>
    public ParticipantImportScope WithMergeInto(IReadOnlyDictionary<int, Guid> mergeInto) => new()
    {
        Mode = Mode,
        TargetDayNumber = TargetDayNumber,
        LinkField = LinkField,
        UpdateFields = UpdateFields,
        MergeInto = mergeInto,
        ExcludedRows = ExcludedRows,
        UpdatePayment = UpdatePayment
    };

    /// <summary>A copy of this scope with <see cref="ExcludedRows"/> replaced.</summary>
    public ParticipantImportScope WithExcludedRows(IReadOnlySet<int> excludedRows) => new()
    {
        Mode = Mode,
        TargetDayNumber = TargetDayNumber,
        LinkField = LinkField,
        UpdateFields = UpdateFields,
        MergeInto = MergeInto,
        ExcludedRows = excludedRows,
        UpdatePayment = UpdatePayment
    };

    /// <summary>A copy of this scope with <see cref="UpdatePayment"/> replaced.</summary>
    public ParticipantImportScope WithUpdatePayment(bool updatePayment) => new()
    {
        Mode = Mode,
        TargetDayNumber = TargetDayNumber,
        LinkField = LinkField,
        UpdateFields = UpdateFields,
        MergeInto = MergeInto,
        ExcludedRows = ExcludedRows,
        UpdatePayment = updatePayment
    };
}

/// <summary>
/// The participant-level (day-independent) fields a current-day-only import is allowed to overwrite on an
/// existing, matched athlete. A bit flag per column so the modal can offer them as a checklist; day-scoped
/// data (group, chip) is not here because it always belongs to the target day and is set unconditionally.
/// </summary>
[Flags]
public enum ParticipantUpdateFields
{
    None = 0,
    FullName = 1 << 0,
    Number = 1 << 1,
    Team = 1 << 2,
    BirthDate = 1 << 3,
    Region = 1 << 4,
    Club = 1 << 5,
    Dussh = 1 << 6,
    Rank = 1 << 7,
    Coach = 1 << 8,
    Representative = 1 << 9,
    FsouCode = 1 << 10,
    IsFsouMember = 1 << 11,
    Payment = 1 << 12
}

/// <summary>Day-mapping mode of a participant import (see <see cref="ParticipantImportScope"/>).</summary>
public enum ParticipantImportMode
{
    AllDays,
    CurrentDayOnly
}

/// <summary>Field used to link an imported row to an existing participant from another day.</summary>
public enum ParticipantLinkField
{
    /// <summary>Match on non-blank FOU code (case-insensitive). The default.</summary>
    FsouCode,

    /// <summary>Match on full name — «Прізвище Ім'я» (case-insensitive, whitespace-normalised).</summary>
    FullName
}
