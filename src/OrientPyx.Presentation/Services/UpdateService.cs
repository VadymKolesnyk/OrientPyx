using OrientPyx.BusinessLogic.Interfaces;
using Velopack;
using Velopack.Sources;

namespace OrientPyx.Presentation.Services;

/// <summary>
/// Velopack-backed <see cref="IUpdateService"/> reading releases from a GitHub repository. See
/// <c>build/publish.ps1</c> for how packages are produced and uploaded. Best-effort throughout: a
/// failed check or download is logged and swallowed so it can never interrupt competition work.
/// </summary>
public sealed class UpdateService : IUpdateService
{
    // The public repository that hosts the Velopack releases (GitHub Releases feed). Update if the repo moves.
    private const string RepositoryUrl = "https://github.com/VadymKolesnyk/OrientPyx";

    private readonly IActivityLog _log;
    private readonly UpdateManager? _manager;
    private UpdateInfo? _pending;

    public UpdateService(IActivityLog log)
    {
        _log = log;

        // The manager reads the local Velopack install layout on construction. For a dev/xcopy build
        // there's no layout, so IsInstalled is false and every public method below short-circuits.
        try
        {
            var manager = new UpdateManager(new GithubSource(RepositoryUrl, accessToken: null, prerelease: false));
            if (manager.IsInstalled)
                _manager = manager;
        }
        catch (Exception ex)
        {
            _log.Error("UpdateService init", ex);
        }
    }

    public bool IsInstalled => _manager is not null;

    public async Task<string?> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        if (_manager is null)
            return null;

        try
        {
            _pending = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
            var version = _pending?.TargetFullRelease.Version.ToString();
            if (version is not null)
                _log.Info($"Update available: {version}");
            return version;
        }
        catch (Exception ex)
        {
            _log.Error("UpdateService check", ex);
            return null;
        }
    }

    public async Task<bool> DownloadAndRestartAsync(CancellationToken cancellationToken = default)
    {
        if (_manager is null || _pending is null)
            return false;

        try
        {
            await _manager.DownloadUpdatesAsync(_pending).ConfigureAwait(false);
            _log.Action($"Applying update {_pending.TargetFullRelease.Version} and restarting.");
            // Replaces the process: applies the staged update, then relaunches the new version.
            _manager.ApplyUpdatesAndRestart(_pending);
            return true;
        }
        catch (Exception ex)
        {
            _log.Error("UpdateService apply", ex);
            return false;
        }
    }
}
