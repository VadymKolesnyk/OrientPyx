namespace OrientPyx.BusinessLogic.Entities;

/// <summary>
/// A relay team on one day (Р-20): belongs to one day and one group; its number is unique within the day. The
/// team's runners are the day's <see cref="ParticipantDay"/> links pointing here (<see cref="ParticipantDay.RelayTeamId"/>),
/// one per leg (<see cref="ParticipantDay.Leg"/>).
/// </summary>
public class RelayTeam
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Owning <see cref="EventDay"/> id (foreign key by convention; no navigation).</summary>
    public Guid EventDayId { get; set; }

    /// <summary>The relay group the team runs in on the day.</summary>
    public Guid GroupId { get; set; }

    /// <summary>Team number, unique within the day (100 in the leg number «100.2»).</summary>
    public int Number { get; set; }

    public string Name { get; set; } = string.Empty;

    public Guid? ClubId { get; set; }

    public Guid? RegionId { get; set; }

    /// <summary>The whole team runs «поза конкурсом»: its time shows, it takes no place.</summary>
    public bool OutOfCompetition { get; set; }

    /// <summary>A judge's status ruling for the whole team (Р-33); null = computed from the legs.</summary>
    public FinishStatus? StatusOverride { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}
