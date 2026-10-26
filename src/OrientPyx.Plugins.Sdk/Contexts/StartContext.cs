namespace OrientPyx.Plugins.Sdk;

/// <summary>What the discipline's start resolution needs to decide where a runner's time starts.</summary>
public sealed class StartContext
{
    /// <summary>The chip's own start box from the read-out; null when none was punched.</summary>
    public DateTimeOffset? ChipStart { get; init; }

    /// <summary>The assigned start (time of day, from the draw / manual entry); null when none.</summary>
    public TimeSpan? AssignedStart { get; init; }

    /// <summary>The read-out's finish time — supplies the date for <see cref="AssignedStart"/>.</summary>
    public DateTimeOffset? FinishTime { get; init; }
}
