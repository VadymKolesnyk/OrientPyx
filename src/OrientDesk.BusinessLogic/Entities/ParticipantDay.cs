namespace OrientDesk.BusinessLogic.Entities;

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

    /// <summary>Team name (used by the rogaine discipline); empty otherwise.</summary>
    public string Team { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}
