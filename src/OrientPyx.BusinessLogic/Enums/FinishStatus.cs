namespace OrientPyx.BusinessLogic.Enums;

/// <summary>
/// The outcome of a participant's run on a day, derived from their read-out (and, later, settable
/// manually). Computed statuses come from the discipline's finish evaluation; manual ones
/// (<see cref="Dns"/>, <see cref="Dsq"/>) are reserved for a judge to set and are not produced
/// automatically yet.
/// </summary>
public enum FinishStatus
{
    /// <summary>No status yet (no read-out, or the discipline doesn't evaluate this way).</summary>
    None,

    /// <summary>Valid run: required controls in order, finished, within the time limit.</summary>
    Ok,

    /// <summary>Missing point / wrong order — a required control is absent or punched out of order.</summary>
    Mp,

    /// <summary>Over the time limit (контрольний час) — finished, controls ok, but too slow.</summary>
    Ovt,

    /// <summary>Did not finish — no finish punch.</summary>
    Dnf,

    /// <summary>Did not start (manual; reserved).</summary>
    Dns,

    /// <summary>Disqualified (manual; reserved).</summary>
    Dsq
}
