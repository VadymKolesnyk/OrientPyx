using OrientDesk.BusinessLogic.Models;

namespace OrientDesk.BusinessLogic.Interfaces;

public interface IPunchImportService
{
    /// <summary>Returns imported punches. Real SportIdent parsing is not implemented yet.</summary>
    Task<IReadOnlyList<PunchRecord>> GetImportedPunchesAsync(CancellationToken cancellationToken = default);
}
