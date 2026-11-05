using OrientPyx.BusinessLogic.Entities;

namespace OrientPyx.BusinessLogic.Interfaces;

/// <summary>
/// The app-database tables the results computation depends on (points rules, sports ranks, the rank qualification
/// table and the rank-validity conditions). Locally this reads <see cref="IAppStore"/>; on a station it reads the
/// <b>host's</b> tables, so two machines never compute different points or ranks for the same day. The pages that edit
/// these tables («Очки», «Розряди») are read-only on a station.
/// </summary>
public interface IResultsAppData
{
    Task<IReadOnlyList<PointsRule>> GetPointsRulesAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SportRank>> GetRanksAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RankQualificationRow>> GetRankQualificationAsync(CancellationToken cancellationToken = default);

    Task<(int MinParticipants, int MinRegions, int CountForRank)?> GetRankConditionsAsync(CancellationToken cancellationToken = default);
}
