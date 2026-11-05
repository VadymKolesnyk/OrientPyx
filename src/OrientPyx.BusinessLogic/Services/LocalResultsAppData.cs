using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Interfaces;

namespace OrientPyx.BusinessLogic.Services;

/// <summary>The local <see cref="IResultsAppData"/>: this machine's own app database.</summary>
public sealed class LocalResultsAppData : IResultsAppData
{
    private readonly IAppStore _appStore;

    public LocalResultsAppData(IAppStore appStore) => _appStore = appStore;

    public Task<IReadOnlyList<PointsRule>> GetPointsRulesAsync(CancellationToken cancellationToken = default)
        => _appStore.GetPointsRulesAsync(cancellationToken);

    public Task<IReadOnlyList<SportRank>> GetRanksAsync(CancellationToken cancellationToken = default)
        => _appStore.GetRanksAsync(cancellationToken);

    public Task<IReadOnlyList<RankQualificationRow>> GetRankQualificationAsync(CancellationToken cancellationToken = default)
        => _appStore.GetRankQualificationAsync(cancellationToken);

    public Task<(int MinParticipants, int MinRegions, int CountForRank)?> GetRankConditionsAsync(CancellationToken cancellationToken = default)
        => _appStore.GetRankConditionsAsync(cancellationToken);
}
