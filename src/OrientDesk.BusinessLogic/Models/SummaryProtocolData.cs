namespace OrientDesk.BusinessLogic.Models;

/// <summary>
/// Raw data for the multi-day summary protocol: every day in the competition, and every group with its
/// members and each member's per-day computed result. The layer-neutral <c>ISummaryProtocolBuilder</c> turns
/// this — plus the user's settings + localized labels — into a renderable <see cref="SummaryProtocolDocument"/>.
/// A participant is grouped by the group they run in; the builder takes the participant's group from the FIRST
/// counted day they have a membership on (the group is normally constant across days).
/// </summary>
public sealed record SummaryProtocolData(
    IReadOnlyList<SummaryProtocolDay> Days,
    IReadOnlyList<SummaryProtocolGroup> Groups,
    ProtocolOfficialsData Officials)
{
    public SummaryProtocolData(IReadOnlyList<SummaryProtocolDay> Days, IReadOnlyList<SummaryProtocolGroup> Groups)
        : this(Days, Groups, ProtocolOfficialsData.None) { }

    public static readonly SummaryProtocolData Empty = new([], [], ProtocolOfficialsData.None);
}

/// <summary>One competition day, used to label the per-day column bands ("День 1 (30 травня)").</summary>
public sealed record SummaryProtocolDay(Guid Id, int Number, DateTimeOffset? Date);

/// <summary>One group's members in the summary. Order mirrors the day-grid group order.</summary>
public sealed record SummaryProtocolGroup(string Name, int Order, IReadOnlyList<SummaryProtocolParticipant> Members);

/// <summary>
/// One participant in a group's summary: their identity fields plus the per-day computed result keyed by day id.
/// A day the participant did not run (no membership / no result) is simply absent from <see cref="ResultsByDay"/>.
/// </summary>
public sealed record SummaryProtocolParticipant(
    Guid ParticipantId,
    string Number,
    string FullName,
    DateTimeOffset? BirthDate,
    string ClubName,
    string RegionName,
    string DusshName,
    string Coach,
    string Rank,
    IReadOnlyDictionary<Guid, ParticipantDayResult> ResultsByDay);
