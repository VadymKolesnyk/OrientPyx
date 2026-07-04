namespace OrientPyx.BusinessLogic.Models;

/// <summary>
/// One step a long-running import reports as it works, layer-neutral so BusinessLogic can raise it
/// without knowing about localization or UI. Presentation maps each <see cref="Stage"/> to a
/// localized line for the on-screen progress log. <see cref="Current"/>/<see cref="Total"/> are
/// populated for the row-by-row <see cref="ImportStage.Participants"/> stage and 0 otherwise.
/// </summary>
/// <param name="Stage">Which phase of the import this update describes.</param>
/// <param name="Current">Items processed so far (participants imported), for progress stages.</param>
/// <param name="Total">Total items expected, for progress stages.</param>
public readonly record struct ImportProgress(ImportStage Stage, int Current = 0, int Total = 0)
{
    public static ImportProgress Of(ImportStage stage) => new(stage);
    public static ImportProgress Counted(ImportStage stage, int current, int total) => new(stage, current, total);
}

/// <summary>The phases a participant import passes through, in order.</summary>
public enum ImportStage
{
    /// <summary>File parsed; <see cref="ImportProgress.Total"/> participants found.</summary>
    Parsed,

    /// <summary>Missing competition days were created (<see cref="ImportProgress.Current"/> = count).</summary>
    DaysCreated,

    /// <summary>The participant database was wiped (the "clear first" option).</summary>
    Cleared,

    /// <summary>Resolving the referenced regions, clubs, sports schools and groups.</summary>
    ResolvingLookups,

    /// <summary>Writing participants — Current of Total done.</summary>
    Participants,

    /// <summary>Everything has been committed.</summary>
    Done
}
