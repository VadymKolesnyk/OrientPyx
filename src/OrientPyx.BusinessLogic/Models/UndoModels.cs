namespace OrientPyx.BusinessLogic.Models;

/// <summary>How a revert of one undo step ended (see <c>IEventStore.RevertUndoStepAsync</c>).</summary>
public enum UndoRevertStatus
{
    /// <summary>The step was reverted (the revert is itself a new step).</summary>
    Done,

    /// <summary>The step changed nothing — skip it silently.</summary>
    Empty,

    /// <summary>Everything the step changed has since been put back by something else; the database is untouched.</summary>
    AlreadyReverted,

    /// <summary>Something the step changed has changed again (or a rule would break); nothing was changed.</summary>
    Conflict,

    /// <summary>A row the revert would change lies on a closed day; nothing was changed, the step stays undoable.</summary>
    DayLocked,

    /// <summary>The step made a bulk write the journal could not see (or was too large).</summary>
    NotUndoable,

    /// <summary>The journal never saw the step (nothing reached the store) or it belongs to someone else.</summary>
    NotFound,

    /// <summary>The step was pushed out of the journal by newer ones.</summary>
    Evicted,

    /// <summary>A database error; nothing was changed.</summary>
    Failed,
}

/// <summary>Why one row stands in the way of a revert.</summary>
public enum UndoConflictKind
{
    /// <summary>A value the step set has changed again to a third value.</summary>
    Changed,

    /// <summary>A row the step changed has been deleted since.</summary>
    RowDeleted,

    /// <summary>A row the step deleted is back under the same key, or a unique index refused the revert.</summary>
    KeyTaken,

    /// <summary>The value the revert would put back is now held by another row (a bib, a chip, a name).</summary>
    ValueTaken,

    /// <summary>A row the revert puts back points at a day/group/team/… that no longer exists.</summary>
    MissingReference,

    /// <summary>A row the revert would delete is referenced by rows the step did not make.</summary>
    StillReferenced,
}

/// <summary>
/// One reason a revert was refused — data, not text: the client words it in its own language. <see cref="Entity"/> is
/// the entity's type name (<c>ParticipantDay</c>), <see cref="Row"/> a readable name of the row (a full name, a team
/// number, a group name; the key when there is none), <see cref="Field"/> a property name, and the two values are in
/// an invariant form with references resolved to names.
/// </summary>
public sealed record UndoConflict(
    UndoConflictKind Kind, string Entity, string? Row, string? Field, string? Expected, string? Actual);

/// <summary>The outcome of <c>IEventStore.RevertUndoStepAsync</c>.</summary>
/// <param name="ChangedRows">How many rows the revert wrote.</param>
/// <param name="LockedDayNumber">The closed day for <see cref="UndoRevertStatus.DayLocked"/>.</param>
/// <param name="RowIds">The Guid keys of the rows the revert wrote (and their participants), so a page can select them.</param>
public sealed record UndoRevertResult(
    UndoRevertStatus Status,
    IReadOnlyList<UndoConflict> Conflicts,
    int ChangedRows,
    int? LockedDayNumber,
    IReadOnlyList<Guid>? RowIds = null)
{
    public static UndoRevertResult Of(UndoRevertStatus status) => new(status, [], 0, null);
}

/// <summary>The result of <c>IEditHistoryService.RevertAsync</c>: the revert and the id of the step it made.</summary>
public sealed record UndoOutcome(UndoRevertResult Result, Guid RevertStepId);
