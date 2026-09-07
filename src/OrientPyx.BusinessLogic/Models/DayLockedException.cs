namespace OrientPyx.BusinessLogic.Models;

/// <summary>
/// Thrown when a write is attempted against a day the user has closed for editing
/// (<see cref="Entities.EventDay.IsLocked"/>). The UI blocks these edits up front, so this is the
/// backstop that keeps a path which slipped past the UI (bulk edit, an import, an auto read-out)
/// from touching a finished day.
/// </summary>
public sealed class DayLockedException : InvalidOperationException
{
    public DayLockedException(int dayNumber)
        : base($"Day {dayNumber} is locked for editing.")
    {
        DayNumber = dayNumber;
    }

    /// <summary>The 1-based number of the locked day, for the message shown to the user.</summary>
    public int DayNumber { get; }
}
