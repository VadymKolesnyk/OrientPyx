using OrientDesk.BusinessLogic.Interfaces;
using OrientDesk.BusinessLogic.Models;

namespace OrientDesk.BusinessLogic.Services;

/// <summary>Placeholder import service. Real SportIdent parsing is not implemented yet.</summary>
public sealed class PunchImportService : IPunchImportService
{
    public Task<IReadOnlyList<PunchRecord>> GetImportedPunchesAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<PunchRecord> punches = [];
        return Task.FromResult(punches);
    }
}
