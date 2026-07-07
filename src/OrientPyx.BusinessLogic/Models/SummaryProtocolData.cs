namespace OrientPyx.BusinessLogic.Models;

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
/// The ranked outcome of one group's summary, exposed by the builder so other consumers (the winners printout)
/// can reuse the exact cross-day aggregation/ranking without re-implementing it. Entries are in printed order:
/// the ranked members first (ascending place, ties sharing a place), then the поза конкурсом members (place null).
/// <see cref="TotalText"/> is the group's formatted «Сума» for the entry (total points or total time per mode).
/// </summary>
public sealed record SummaryRankedGroup(string GroupName, int Order, IReadOnlyList<SummaryRankedEntry> Entries);

/// <summary>One ranked member of a summary group: the member, their place (null when поза конкурсом), and the
/// formatted total shown in the «Сума» column.</summary>
public sealed record SummaryRankedEntry(SummaryProtocolParticipant Member, int? Place, string TotalText);

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
