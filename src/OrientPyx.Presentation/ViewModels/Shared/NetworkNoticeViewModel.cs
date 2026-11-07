using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models.Network;
using OrientPyx.Localization;
using OrientPyx.Presentation.Services;

namespace OrientPyx.Presentation.ViewModels.Shared;

/// <summary>
/// The station's network notices above every page:
/// <list type="bullet">
/// <item>the «Немає зв'язку з хостом» banner — only after the host has not answered anything for a while
/// (<see cref="StationState.Reconnecting"/>; a blip of a few seconds never shows it). The shell also disables the page
/// content while it is up, so the last snapshot stays visible but nothing can be edited; when the host answers again
/// every page reloads by itself;</item>
/// <item>the small «Зберігається…» pill while writes wait for the host — the page stays editable, they retry by
/// themselves;</item>
/// <item>a toast when a write gave up after its retry window, so a lost change is never silent.</item>
/// </list>
/// </summary>
public sealed partial class NetworkNoticeViewModel : ObservableObject
{
    // A burst of abandoned writes (one outage) makes one toast, not five.
    private static readonly TimeSpan ToastCooldown = TimeSpan.FromSeconds(10);

    private readonly INetworkClient _client;
    private readonly IWorkspaceMode _mode;
    private readonly IToastService _toasts;
    private DateTimeOffset _lastToast = DateTimeOffset.MinValue;

    public NetworkNoticeViewModel(ILocalizationService localization, INetworkClient client, IWorkspaceMode mode, IToastService toasts)
    {
        Localization = localization;
        _client = client;
        _mode = mode;
        _toasts = toasts;
        // These events may fire on a transport thread.
        _client.Changed += (_, _) => Dispatcher.UIThread.Post(Raise);
        _client.WriteFailed += (_, _) => Dispatcher.UIThread.Post(ShowWriteFailed);
        _mode.Changed += (_, _) => Dispatcher.UIThread.Post(Raise);
        Localization.PropertyChanged += (_, _) => Raise();
    }

    public ILocalizationService Localization { get; }

    /// <summary>True while the station cannot reach its host (editing is blocked until it is back).</summary>
    public bool IsOffline => _mode.IsStation && _client.State == StationState.Reconnecting;

    /// <summary>True while the page content may be edited (the inverse, for the shell's IsEnabled binding).</summary>
    public bool IsOnline => !IsOffline;

    public string Text => string.Format(Localization.Get("Banner.NetworkOffline"), HostName);

    /// <summary>True while some write has been waiting for the host for more than a moment (and the banner is not up).</summary>
    public bool IsSaving => _mode.IsStation && !IsOffline && _client.DelayedWrites > 0;

    public string SavingText => string.Format(Localization.Get("Banner.NetworkSaving"), _client.DelayedWrites, HostName);

    private string HostName => _client.Host?.HostName ?? string.Empty;

    private void ShowWriteFailed()
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastToast < ToastCooldown)
            return;
        _lastToast = now;
        _toasts.Show(Localization.Get("Network.WriteFailed.Title"),
            string.Format(Localization.Get("Network.WriteFailed.Message"), HostName));
    }

    private void Raise()
    {
        OnPropertyChanged(nameof(IsOffline));
        OnPropertyChanged(nameof(IsOnline));
        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(IsSaving));
        OnPropertyChanged(nameof(SavingText));
    }
}
