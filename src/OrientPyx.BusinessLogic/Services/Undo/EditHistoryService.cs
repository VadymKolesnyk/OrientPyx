using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Services.Undo;

/// <inheritdoc />
public sealed class EditHistoryService : IEditHistoryService
{
    private readonly ISessionService _session;
    private readonly IEventStore _store;
    private readonly PointsRuleCopySync _pointsRules;
    private readonly IActivityLog? _log;

    public EditHistoryService(ISessionService session, IEventStore store, PointsRuleCopySync pointsRules, IActivityLog? log = null)
    {
        _session = session;
        _store = store;
        _pointsRules = pointsRules;
        _log = log;
    }

    public async Task<UndoOutcome> RevertAsync(Guid stepId, CancellationToken cancellationToken = default)
    {
        if (_session.CurrentEvent is not { } competition)
            return new UndoOutcome(UndoRevertResult.Of(UndoRevertStatus.NotFound), Guid.Empty);

        var revertStep = Guid.NewGuid();
        UndoRevertResult result;
        using (UndoScope.Enter(revertStep))
            result = await _store.RevertUndoStepAsync(competition.FolderPath, stepId, cancellationToken);

        if (result.Status == UndoRevertStatus.Done)
        {
            // CompetitionInfo.PointsRulesJson is derived and never journalled — a reverted group rule choice would leave
            // the copy behind, so it is recomputed here, as a system write outside every step.
            using (UndoScope.Suppress())
            {
                try
                {
                    await _pointsRules.RefreshAsync(competition.FolderPath, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log?.Error("Undo: refreshing the points-rule copy after a revert", ex);
                }
            }
        }
        return new UndoOutcome(result, revertStep);
    }
}
