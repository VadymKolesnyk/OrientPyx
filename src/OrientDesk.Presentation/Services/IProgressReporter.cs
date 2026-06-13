namespace OrientDesk.Presentation.Services;

/// <summary>
/// Lets a long-running operation push human-readable progress lines onto the busy overlay while it
/// works (e.g. "Found 400 participants", "Imported 150 / 400"). Handed to the operation by
/// <see cref="IBusyService.RunAsync{T}(System.Func{IProgressReporter, System.Threading.Tasks.Task{T}})"/>.
/// Implementations marshal the update to the UI thread, so callers may report from a pool thread.
/// </summary>
public interface IProgressReporter
{
    /// <summary>Appends a line to the progress log shown in the overlay.</summary>
    void Report(string line);

    /// <summary>
    /// Replaces the last reported line in place. Use for a counter that ticks
    /// ("Imported 1 / 400" → "Imported 2 / 400") so the log doesn't grow one line per row.
    /// Falls back to appending when there is no previous line.
    /// </summary>
    void ReportReplace(string line);
}
