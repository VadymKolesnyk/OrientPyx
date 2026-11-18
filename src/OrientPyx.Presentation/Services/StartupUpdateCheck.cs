using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.Localization;
using OrientPyx.Presentation.ViewModels.Dialogs;

namespace OrientPyx.Presentation.Services;

/// <summary>
/// The quiet update check of every launch (installed builds only): asks the release feed once, in the background, and
/// when a newer version is out — and it is not the one «Пропустити цю версію» was pressed for — shows a clickable toast
/// «Доступна нова версія»; its click opens <see cref="UpdateAvailableViewModel"/>. Nothing at all is shown when the app is
/// up to date, offline or the check fails (that goes to the activity log only).
/// </summary>
public sealed class StartupUpdateCheck
{
    private readonly IUpdateService _updates;
    private readonly IUiPreferencesService _preferences;
    private readonly IToastService _toasts;
    private readonly IDialogService _dialogs;
    private readonly ILocalizationService _localization;
    private readonly IActivityLog _log;

    public StartupUpdateCheck(
        IUpdateService updates,
        IUiPreferencesService preferences,
        IToastService toasts,
        IDialogService dialogs,
        ILocalizationService localization,
        IActivityLog log)
    {
        _updates = updates;
        _preferences = preferences;
        _toasts = toasts;
        _dialogs = dialogs;
        _localization = localization;
        _log = log;
    }

    /// <summary>Checks once; never throws. Call after the window is up and the startup screen settled.</summary>
    public async Task RunAsync()
    {
        if (!_updates.IsInstalled)
            return;

        try
        {
            // Off the UI thread: Velopack reads the local packages folder before it goes to the network.
            var version = await Task.Run(() => _updates.CheckForUpdateAsync());
            if (version is null)
                return;
            if (string.Equals(version, _preferences.SkippedUpdateVersion, StringComparison.OrdinalIgnoreCase))
            {
                _log.Info($"Update {version} is available but was skipped by the user.");
                return;
            }

            _toasts.Show(
                _localization.Get("UpdateAvailable.Toast.Title"),
                string.Format(_localization.Get("UpdateAvailable.Toast.Message"), version),
                () => _ = OpenAsync(version));
        }
        catch (Exception ex)
        {
            _log.Error("Start-up update check", ex);
        }
    }

    private async Task OpenAsync(string version)
    {
        try
        {
            _log.Action($"Opened the «update available» dialog for {version}.");
            var choice = await _dialogs.ShowUpdateAvailableAsync(
                new UpdateAvailableViewModel(_localization, _updates, version));
            if (choice == UpdateAvailableChoice.Skip)
            {
                _preferences.SkippedUpdateVersion = version;
                _log.Action($"Skipped the update {version}.");
            }
        }
        catch (Exception ex)
        {
            _log.Error("Update available dialog", ex);
        }
    }
}
