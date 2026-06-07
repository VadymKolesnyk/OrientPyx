using System.Reflection;
using OrientDesk.BusinessLogic.Interfaces;
using OrientDesk.BusinessLogic.Models;

namespace OrientDesk.BusinessLogic.Services;

/// <summary>Placeholder competition service returning static dashboard data.</summary>
public sealed class CompetitionService : ICompetitionService
{
    public Task<DashboardInfo> GetDashboardInfoAsync(CancellationToken cancellationToken = default)
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";

        var info = new DashboardInfo
        {
            ApplicationName = "OrientDesk",
            Version = version,
            Status = "Готово",
            ParticipantCount = 3,
            GroupCount = 2
        };

        return Task.FromResult(info);
    }
}
