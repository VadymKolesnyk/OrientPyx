using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.Localization;
using OrientPyx.Presentation.Services;

namespace OrientPyx.Presentation.ViewModels.Dialogs;

/// <summary>
/// «Налаштування сповіщень» (the bell on the «Зчитка на фініші» auto-read panel): how the finish judge is told about a
/// rental chip due back (toast, Windows notification, «дзінь-дон») and an unknown chip (Windows notification, three
/// beeps the moment it is read), and whether those alerts flash the taskbar button while the window is minimized.
/// Every switch is written straight to this computer's <see cref="IUiPreferencesService"/> — there is nothing to save;
/// the eye shows an example of the toast / Windows notification, «▶» plays the sound to check the volume.
/// </summary>
public sealed partial class FinishAlertsViewModel : ObservableObject
{
    private readonly TaskCompletionSource<bool> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    // The chips of the examples — made up, like the runner (whose name and group are translated).
    private const string SampleRentalChip = "8001023";
    private const string SampleRunnerNumber = "57";
    private const string SampleUnknownChip = "8001099";

    private readonly IUiPreferencesService _preferences;
    private readonly IDesktopAlerts _alerts;
    private readonly IToastService _toasts;

    public FinishAlertsViewModel(
        ILocalizationService localization, IUiPreferencesService preferences, IDesktopAlerts alerts, IToastService toasts)
    {
        Localization = localization;
        _preferences = preferences;
        _alerts = alerts;
        _toasts = toasts;
        _collectChipToast = preferences.CollectChipToast;
        _collectChipSound = preferences.CollectChipSound;
        _unknownChipSound = preferences.UnknownChipSound;
        _collectChipSystemNotification = preferences.CollectChipSystemNotification;
        _unknownChipSystemNotification = preferences.UnknownChipSystemNotification;
        _flashWhenInactive = preferences.FlashWhenInactive;
    }

    public ILocalizationService Localization { get; }

    /// <summary>False where the OS gives us no way to play a sound — the sound switches and «▶» are disabled.</summary>
    public bool CanPlaySound => _alerts.CanPlaySound;

    /// <summary>False where the OS has no taskbar flash / Dock bounce — the switch is disabled.</summary>
    public bool CanRequestAttention => _alerts.CanRequestAttention;

    /// <summary>Windows 10 1809+ only — elsewhere the «Сповіщення Windows» column is not shown.</summary>
    public bool CanShowNotifications => _alerts.CanShowNotifications;

    [ObservableProperty]
    private bool _collectChipToast;

    [ObservableProperty]
    private bool _collectChipSound;

    [ObservableProperty]
    private bool _unknownChipSound;

    [ObservableProperty]
    private bool _collectChipSystemNotification;

    [ObservableProperty]
    private bool _unknownChipSystemNotification;

    [ObservableProperty]
    private bool _flashWhenInactive;

    partial void OnCollectChipToastChanged(bool value) => _preferences.CollectChipToast = value;

    partial void OnCollectChipSoundChanged(bool value) => _preferences.CollectChipSound = value;

    partial void OnUnknownChipSoundChanged(bool value) => _preferences.UnknownChipSound = value;

    partial void OnCollectChipSystemNotificationChanged(bool value) => _preferences.CollectChipSystemNotification = value;

    partial void OnUnknownChipSystemNotificationChanged(bool value) => _preferences.UnknownChipSystemNotification = value;

    partial void OnFlashWhenInactiveChanged(bool value) => _preferences.FlashWhenInactive = value;

    /// <summary>Completes when the dialog is closed (the result is unused — every switch is already saved).</summary>
    public Task<bool> Completion => _completion.Task;

    [RelayCommand]
    private void PreviewCollectChip() => _alerts.Preview(FinishAlert.CollectChip);

    /// <summary>The eye in «У програмі»: the toast in the window's corner, as a read-out would show it.</summary>
    [RelayCommand]
    private void ShowCollectChipToast()
    {
        var (title, message) = SampleCollectChip();
        _toasts.Show(title, message);
    }

    /// <summary>The eye in «Сповіщення Windows»: the Windows notification, shown now although the window is active.</summary>
    [RelayCommand]
    private void ShowCollectChipNotification()
    {
        var (title, message) = SampleCollectChip();
        _alerts.PreviewNotification(FinishAlert.CollectChip, title, message);
    }

    [RelayCommand]
    private void ShowUnknownChipNotification()
    {
        var (title, message) = FinishAlertTexts.UnknownChip(Localization, SampleUnknownChip);
        _alerts.PreviewNotification(FinishAlert.UnknownChip, title, message);
    }

    /// <summary>The eye of the flash switch: minimizes the window and flashes its taskbar button.</summary>
    [RelayCommand]
    private void ShowFlash() => _alerts.PreviewAttention();

    private (string Title, string Message) SampleCollectChip()
        => FinishAlertTexts.CollectChip(Localization, SampleRentalChip, SampleRunnerNumber,
            Localization.Get("FinishAlerts.Sample.Runner"), Localization.Get("FinishAlerts.Sample.Group"));

    [RelayCommand]
    private void PreviewUnknownChip() => _alerts.Preview(FinishAlert.UnknownChip);

    [RelayCommand]
    private void Close() => _completion.TrySetResult(true);
}
