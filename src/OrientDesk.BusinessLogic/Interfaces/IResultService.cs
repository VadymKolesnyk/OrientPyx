using OrientDesk.BusinessLogic.Models;

namespace OrientDesk.BusinessLogic.Interfaces;

public interface IResultService
{
    /// <summary>Returns result rows. Real result calculation is not implemented yet.</summary>
    Task<IReadOnlyList<ResultRow>> GetResultsAsync(CancellationToken cancellationToken = default);
}
