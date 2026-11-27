using OrientPyx.BusinessLogic.Entities;

namespace OrientPyx.BusinessLogic.Models.Mcp;

/// <summary>
/// Everything the analytics tables of one day are built from (plan §5.2), read from one day snapshot: the raw rows,
/// the computed results per participant-day link (the same pipeline as the protocols) and each link's passage
/// (<see cref="SplitsView"/>, the same as the split export).
/// </summary>
public sealed record AnalyticsDayData(
    EventDay Day,
    EventDaySnapshot Snapshot,
    IReadOnlyDictionary<Guid, ParticipantDayResult> ResultsByLink,
    IReadOnlyDictionary<Guid, SplitsView> SplitsByLink);
