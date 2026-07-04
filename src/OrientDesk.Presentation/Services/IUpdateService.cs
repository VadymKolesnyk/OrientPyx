namespace OrientDesk.Presentation.Services;

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
    /// Checks the release feed for a newer version. Returns the new version string when one is available,
    /// or null when up to date, not installed, or the check failed (failures are swallowed — an update
    /// check must never break the app; details go to the activity log).
    /// </summary>
    Task<string?> CheckForUpdateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads and applies the pending update found by the most recent <see cref="CheckForUpdateAsync"/>,
    /// then restarts the app into the new version. Does nothing if no update is pending. Returns false and
    /// leaves the app running if the update could not be applied.
    /// </summary>
    Task<bool> DownloadAndRestartAsync(CancellationToken cancellationToken = default);
}
