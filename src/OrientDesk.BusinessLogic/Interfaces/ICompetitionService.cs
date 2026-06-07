using OrientDesk.BusinessLogic.Models;

namespace OrientDesk.BusinessLogic.Interfaces;

public interface ICompetitionService
{
    Task<DashboardInfo> GetDashboardInfoAsync(CancellationToken cancellationToken = default);
}
