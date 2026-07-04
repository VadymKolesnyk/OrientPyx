using OrientPyx.BusinessLogic.Enums;

namespace OrientPyx.BusinessLogic.Entities;

/// <summary>
/// A participant's per-day record. The mere existence of this row means "this participant runs on
/// this day"; deleting it removes the participant from the day only. Group and chip are optional
/// fields scoped to this day (a person may run a different group, or no group, on each day). The
/// chip is unique per day when non-blank (enforced in the service layer).
/// </summary>
public class ParticipantDay
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Owning <see cref="EventDay"/> id (foreign key by convention; no navigation).</summary>
    public Guid EventDayId { get; set; }

    /// <summary>Owning <see cref="Participant"/> id (foreign key by convention; no navigation).</summary>
    public Guid ParticipantId { get; set; }

    /// <summary>Stable display/sort order within the day's grid.</summary>
    public int Order { get; set; }

    /// <summary>Group assignment for this day; null when not yet assigned (still a member of the day).</summary>
    public Guid? GroupId { get; set; }

    /// <summary>Chip number for this day; optional, free text. Unique per day when non-blank.</summary>
    public string Chip { get; set; } = string.Empty;

    /// <summary>Start time (time of day) for this day; null when not set. Per-day, member-only.</summary>
    public TimeSpan? StartTime { get; set; }

    /// <summary>Whether the participant runs out of competition (поза конкурсом) this day. Per-day, member-only.</summary>
    public bool OutOfCompetition { get; set; }

    /// <summary>
    /// A judge's manual points correction for a point-scoring day (rogaine / score formats): added to the
    /// computed «Бали» total. May be positive or negative; null means "not entered" (no correction).
    /// Distinct from null vs 0 matters for rogaine, where the team correction is the smallest entered
    /// bonus among the team's members (an un-entered member doesn't drag the team's bonus to 0). Held here
    /// per participant-day; like <see cref="ResultStatusOverride"/> it has its own writer so the debounced
    /// row save can't wipe it.
    /// </summary>
    public int? Bonus { get; set; }

    /// <summary>
    /// A judge's manual finish-status override for this participant on this day. When set, it wins over
    /// the status derived from the chip's read-out (so the participant tables show the manually-set
    /// OK/DNS/DSQ/… instead of the computed one); null leaves the status to automatic evaluation. Held
    /// here (per participant-day) rather than on a <c>FinishReadout</c> row so it stays stable when the
    /// chip is re-read and a newer read-out row appears.
    /// </summary>
    public FinishStatus? ResultStatusOverride { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}
