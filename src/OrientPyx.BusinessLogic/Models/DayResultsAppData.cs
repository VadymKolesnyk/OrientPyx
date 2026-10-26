using OrientPyx.BusinessLogic.Entities;

namespace OrientPyx.BusinessLogic.Models;

/// <summary>
/// The app-database inputs of a day's result computation — points rules and the sports-rank tables. Read once
/// per computation (once for all days in a multi-day view) and handed to the calculator with the day snapshot.
/// The rank tables are only loaded when some group on the day awards ranks (<see cref="HasRankData"/>).
/// </summary>
public sealed record DayResultsAppData
{
    public IReadOnlyList<PointsRule> PointsRules { get; init; } = [];

    /// <summary>True when the rank tables below were loaded.</summary>
    public bool HasRankData { get; init; }

    public IReadOnlyList<RankQualificationRow> RankQualification { get; init; } = [];

    public (int MinParticipants, int MinRegions, int CountForRank) RankConditions { get; init; } = (3, 8, 12);

    public IReadOnlyList<SportRank> Ranks { get; init; } = [];
}
