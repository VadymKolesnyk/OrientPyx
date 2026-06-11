namespace OrientDesk.Presentation.Services;

/// <summary>
/// Periodically reads a file's full text and hands it to a callback, for "watch a readout file and
/// pick up new chips every N seconds" style features. Reading the disk is an infrastructure concern,
/// so it lives in the presentation layer rather than in a business service; parsing the text is left
/// to the caller (an <see cref="OrientDesk.BusinessLogic.Interfaces.IReadoutParser"/>).
///
/// One file at a time: starting a new watch replaces any current one. Read errors are swallowed so a
/// transient lock or a momentarily missing file does not stop the poll.
/// </summary>
public interface IFileReadoutPoller
{
    /// <summary>
    /// Starts polling <paramref name="filePath"/> every <paramref name="interval"/>, invoking
    /// <paramref name="onContent"/> with the file's text on each successful read (the first read
    /// fires immediately). Replaces any current watch. The callback is awaited, so a slow consumer
    /// throttles the next read rather than overlapping with it.
    /// </summary>
    void Start(string filePath, TimeSpan interval, Func<string, Task> onContent);

    /// <summary>Stops the current watch, if any. Safe to call when nothing is running.</summary>
    void Stop();
}
