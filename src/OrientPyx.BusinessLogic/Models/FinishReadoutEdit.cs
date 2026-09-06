using OrientPyx.BusinessLogic.Enums;

namespace OrientPyx.BusinessLogic.Models;

/// <summary>
/// One pickable participant for the finish-read edit modal's "reassign chip" dropdown: the day member's
/// id plus a display label (bib + ПІБ + group). Independent of any UI type.
/// </summary>
/// <param name="ParticipantId">The participant's id (day member).</param>
/// <param name="Number">The participant's bib number (may be blank).</param>
/// <param name="FullName">The participant's full name.</param>
/// <param name="GroupName">The participant's group on the day (may be blank).</param>
public sealed record FinishReadoutParticipantOption(
    Guid ParticipantId,
    string Number,
    string FullName,
    string GroupName);

/// <summary>
/// One pickable entry of a competition-level lookup (group / region / club / ДЮСШ) offered by the
/// finish-read modal's "create a new participant" form. Independent of any UI type.
/// </summary>
/// <param name="Id">The lookup row's id.</param>
/// <param name="Name">The name shown in the dropdown.</param>
public sealed record FinishReadoutLookupOption(Guid Id, string Name);

/// <summary>
/// Everything the finish-read edit modal needs to open for one logged read-out: its current editable
/// values (chip, times, punches, status) and the day's participants the chip can be reassigned to, plus
/// the participant who currently holds the chip on the day (when any) so the dropdown opens on them.
/// </summary>
public sealed class FinishReadoutEditData
{
    /// <summary>The read-out being edited (its stable id).</summary>
    public Guid Id { get; init; }

    /// <summary>The chip number as currently stored.</summary>
    public string ChipNumber { get; init; } = string.Empty;

    /// <summary>Start time, or null when none.</summary>
    public DateTimeOffset? StartTime { get; init; }

    /// <summary>Finish time, or null when none.</summary>
    public DateTimeOffset? FinishTime { get; init; }

    /// <summary>The control punches in order (code + time), as currently stored.</summary>
    public IReadOnlyList<ChipPunch> Punches { get; init; } = [];

    /// <summary>The effective status currently shown (the manual override when set, else the computed one).</summary>
    public FinishStatus Status { get; init; }

    /// <summary>True when the shown status is a manual override (vs the discipline's computed status).</summary>
    public bool HasManualStatus { get; init; }

    /// <summary>The day's participants the chip can be reassigned to, ordered for display.</summary>
    public IReadOnlyList<FinishReadoutParticipantOption> Participants { get; init; } = [];

    /// <summary>The participant who currently holds this chip on the day, or null when unrecognised.</summary>
    public Guid? CurrentHolderId { get; init; }

    /// <summary>The day's groups, offered by the "create a new participant" form (a group is mandatory there).</summary>
    public IReadOnlyList<FinishReadoutLookupOption> Groups { get; init; } = [];

    /// <summary>The competition's regions, offered by the "create a new participant" form (optional field).</summary>
    public IReadOnlyList<FinishReadoutLookupOption> Regions { get; init; } = [];

    /// <summary>The competition's clubs, offered by the "create a new participant" form (optional field).</summary>
    public IReadOnlyList<FinishReadoutLookupOption> Clubs { get; init; } = [];

    /// <summary>The competition's sports schools (ДЮСШ), offered by the "create a new participant" form.</summary>
    public IReadOnlyList<FinishReadoutLookupOption> Dusshes { get; init; } = [];

    /// <summary>The application-level rank names, offered by the "create a new participant" form (rank is text).</summary>
    public IReadOnlyList<string> Ranks { get; init; } = [];

    /// <summary>
    /// Bib numbers already taken in the competition, mapped to the holder's full name — so the
    /// "create a new participant" form can reject a duplicate number as the operator types, naming who
    /// holds it. Keyed case-insensitively; numbers are unique per competition, not per day.
    /// </summary>
    public IReadOnlyDictionary<string, string> TakenNumbers { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// The "create a new participant" form of the unknown-chip modal: the identity fields of a competitor
/// who is not yet in the competition, to be created and added to the current day holding the read chip.
/// Only <see cref="FullName"/> and <see cref="GroupId"/> are required — everything else is optional and
/// exists because those fields show up in the protocols.
/// </summary>
public sealed class NewParticipantData
{
    /// <summary>Full name (ПІБ). Required.</summary>
    public string FullName { get; init; } = string.Empty;

    /// <summary>The group on the current day. Required.</summary>
    public Guid GroupId { get; init; }

    /// <summary>Bib / start number; blank when not assigned. A number already taken is dropped.</summary>
    public string Number { get; init; } = string.Empty;

    /// <summary>Date of birth; null when unknown.</summary>
    public DateTimeOffset? BirthDate { get; init; }

    /// <summary>Region id; null = none.</summary>
    public Guid? RegionId { get; init; }

    /// <summary>Club id; null = none.</summary>
    public Guid? ClubId { get; init; }

    /// <summary>ДЮСШ id; null = none.</summary>
    public Guid? DusshId { get; init; }

    /// <summary>Sports rank (text, as everywhere else); blank = none.</summary>
    public string Rank { get; init; } = string.Empty;

    /// <summary>Coach(es); blank = none.</summary>
    public string Coach { get; init; } = string.Empty;
}

/// <summary>
/// The confirmed result of the finish-read edit modal: the edited read-out fields plus, optionally, the
/// participant the chip should be (re)assigned to on the day. <see cref="ManualStatus"/> is the chosen
/// status override (null = leave to automatic evaluation). <see cref="ReassignToParticipantId"/> is null
/// when the chip's holder is left unchanged.
/// </summary>
public sealed class FinishReadoutEdit
{
    public Guid Id { get; init; }
    public string ChipNumber { get; init; } = string.Empty;
    public DateTimeOffset? StartTime { get; init; }
    public DateTimeOffset? FinishTime { get; init; }
    public IReadOnlyList<ChipPunch> Punches { get; init; } = [];
    public FinishStatus? ManualStatus { get; init; }
    public Guid? ReassignToParticipantId { get; init; }

    /// <summary>
    /// Set when the operator filled in the "create a new participant" form instead of picking an existing
    /// one: the competitor is created on the fly, added to the current day, and handed the read chip.
    /// Mutually exclusive with <see cref="ReassignToParticipantId"/>.
    /// </summary>
    public NewParticipantData? NewParticipant { get; init; }
}
