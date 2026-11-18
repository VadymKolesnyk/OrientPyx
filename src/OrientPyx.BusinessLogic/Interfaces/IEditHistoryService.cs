using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Interfaces;

/// <summary>
/// Undo / redo of the operator's edits (Ctrl+Z / Ctrl+Y) on the data level: reverts one journalled step of the
/// session's competition. The steps themselves are opened by the UI (<c>UndoScope</c>); the journal lives in the
/// process that owns <c>event.db</c> (this machine, or the host for a station).
/// </summary>
public interface IEditHistoryService
{
    /// <summary>
    /// Reverts <paramref name="stepId"/> under a new step (whose id comes back, so redo can revert it in turn) and, after
    /// a successful revert, refreshes the competition's copy of its points rules (that column is not journalled).
    /// </summary>
    Task<UndoOutcome> RevertAsync(Guid stepId, CancellationToken cancellationToken = default);
}
