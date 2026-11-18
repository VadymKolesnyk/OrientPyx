namespace OrientPyx.Presentation.Services;

/// <summary>
/// Checks for and applies application updates published by Velopack (see the packaging scripts under
/// <c>build/</c>). Backed by GitHub Releases. A no-op when the running build was not installed by
/// Velopack (e.g. a <c>dotnet run</c> dev build), so it is always safe to call.
/// </summary>
public interface IUpdateService
{
    /// <summary>
    /// True when this build is a Velopack-installed app (so updates are possible). False for a dev/xcopy
    /// build; every method below is a no-op in that case.
    /// </summary>
    bool IsInstalled { get; }

    /// <summary>
    /// Runs right before the app is replaced by the new version (after a successful download): the window owner
    /// flushes the shown page's pending writes and stops the network host / leaves the host, as a normal close
    /// would — <see cref="DownloadAndRestartAsync"/> exits the process without the window's closing handler.
    /// Its failure is logged and does not stop the update.
    /// </summary>
    Func<Task>? BeforeRestart { get; set; }

    /// <summary>
    /// Checks the release feed for a newer version. Returns the new version string when one is available,
    /// or null when up to date, not installed, or the check failed (failures are swallowed — an update
    /// check must never break the app; no connection is a log line, anything else an error in the log).
    /// </summary>
    Task<string?> CheckForUpdateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads and applies the pending update found by the most recent <see cref="CheckForUpdateAsync"/>,
    /// then restarts the app into the new version. Does nothing if no update is pending. Returns false and
    /// leaves the app running if the update could not be applied. <paramref name="progress"/> gets the download's
    /// percentage (0…100) on the thread pool — pass a <see cref="Progress{T}"/> made on the UI thread.
    /// </summary>
    Task<bool> DownloadAndRestartAsync(IProgress<int>? progress = null, CancellationToken cancellationToken = default);
}
