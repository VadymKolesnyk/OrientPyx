using OrientDesk.BusinessLogic.Interfaces;
using OrientDesk.BusinessLogic.Models;

namespace OrientDesk.BusinessLogic.Services;

/// <summary>Placeholder result service. Real result calculation is not implemented yet.</summary>
public sealed class ResultService : IResultService
{
    public Task<IReadOnlyList<ResultRow>> GetResultsAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ResultRow> results = [];
        return Task.FromResult(results);
    }
}
