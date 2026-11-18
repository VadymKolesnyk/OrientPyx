using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.Localization;
using OrientPyx.Presentation.Services;

namespace OrientPyx.Presentation.ViewModels.Dialogs;

/// <summary>What the user did with the «Доступна нова версія» dialog (an installed update never returns: the app restarts).</summary>
public enum UpdateAvailableChoice
{
    /// <summary>«Закрити» / Esc: nothing changes, the next launch offers the version again.</summary>
    Close,

    /// <summary>«Пропустити цю версію»: the start-up check stays silent about this version.</summary>
    Skip,
}

/// <summary>
/// «Доступна нова версія»: opened from the start-up toast (see <c>App.CheckForUpdateWhenReadyAsync</c>). Names the new
/// version, links to its «Що нового», and offers «Пропустити цю версію» / «Завантажити й перезапустити» / «Закрити». The
/// download runs inside the dialog with its progress; a failure keeps the dialog open with a note (the app runs on).
/// </summary>
public sealed partial class UpdateAvailableViewModel : ObservableObject
{
    private readonly IUpdateService _updates;
    private readonly TaskCompletionSource<UpdateAvailableChoice> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public UpdateAvailableViewModel(ILocalizationService localization, IUpdateService updates, string version)
    {
        Localization = localization;
        _updates = updates;
        Version = version;
        Localization.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Message));
            OnPropertyChanged(nameof(ProgressText));
        };
    }

    public ILocalizationService Localization { get; }

    /// <summary>The version on offer, e.g. <c>0.9.1</c>.</summary>
    public string Version { get; }

    /// <summary>The running version, e.g. <c>0.9.0</c> (of the app's own assembly — the entry one is a test host in tests).</summary>
    public string CurrentVersion => typeof(UpdateAvailableViewModel).Assembly.GetName().Version?.ToString(3) ?? "—";

    /// <summary>«Вийшла версія 0.9.1 (у вас 0.9.0).»</summary>
    public string Message => string.Format(Localization.Get("UpdateAvailable.Message"), Version, CurrentVersion);

    /// <summary>This version's changes in «Що нового» — the link in the dialog.</summary>
    public Uri ChangelogUri => DocsLinks.Changelog(Version);

    /// <summary>True while the update downloads: the buttons are disabled and the progress shows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(SkipCommand), nameof(InstallCommand), nameof(CloseCommand))]
    private bool _isDownloading;

    public bool IsIdle => !IsDownloading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    private int _progress;

    /// <summary>«Завантаження… 42 %».</summary>
    public string ProgressText => string.Format(Localization.Get("UpdateAvailable.Downloading"), Progress);

    /// <summary>Set when the download or the restart failed; the dialog stays open so the user can retry or close.</summary>
    [ObservableProperty]
    private bool _failed;

    /// <summary>Completes when the user closes or skips (an install that worked never completes it).</summary>
    public Task<UpdateAvailableChoice> Completion => _completion.Task;

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void Skip() => _completion.TrySetResult(UpdateAvailableChoice.Skip);

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void Close() => _completion.TrySetResult(UpdateAvailableChoice.Close);

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task InstallAsync()
    {
        Failed = false;
        Progress = 0;
        IsDownloading = true;
        // Made here, on the UI thread: Velopack reports from the thread pool, Progress<T> posts back. The download runs
        // off the UI thread — Velopack takes its update lock and unpacks deltas synchronously in places.
        var progress = new Progress<int>(p => Progress = Math.Clamp(p, 0, 100));
        var ok = await Task.Run(() => _updates.DownloadAndRestartAsync(progress));
        // On success the process is replaced by the new version and never gets here.
        IsDownloading = false;
        Failed = !ok;
    }
}
