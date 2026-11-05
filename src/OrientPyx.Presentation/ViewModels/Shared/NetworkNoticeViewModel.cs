using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models.Network;
using OrientPyx.Localization;

namespace OrientPyx.Presentation.ViewModels.Shared;

/// <summary>
/// The «Немає зв'язку з хостом» banner above every page: shown while this station lost its host and is reconnecting
/// (<see cref="StationState.Reconnecting"/>). The shell also disables the page content while it is up, so the last
/// snapshot stays visible but nothing can be edited; when the host answers again the transport replays a full
/// refresh on the change bus and every page reloads by itself.
/// </summary>
public sealed partial class NetworkNoticeViewModel : ObservableObject
{
    private readonly INetworkClient _client;
    private readonly IWorkspaceMode _mode;

    public NetworkNoticeViewModel(ILocalizationService localization, INetworkClient client, IWorkspaceMode mode)
    {
        Localization = localization;
        _client = client;
        _mode = mode;
        // Both events may fire on a transport thread.
        _client.Changed += (_, _) => Dispatcher.UIThread.Post(Raise);
        _mode.Changed += (_, _) => Dispatcher.UIThread.Post(Raise);
        Localization.PropertyChanged += (_, _) => Raise();
    }

    public ILocalizationService Localization { get; }

    /// <summary>True while the station cannot reach its host (editing is blocked until it is back).</summary>
    public bool IsOffline => _mode.IsStation && _client.State == StationState.Reconnecting;

    /// <summary>True while the page content may be edited (the inverse, for the shell's IsEnabled binding).</summary>
    public bool IsOnline => !IsOffline;

    public string Text => string.Format(Localization.Get("Banner.NetworkOffline"), _client.Host?.HostName ?? string.Empty);

    private void Raise()
    {
        OnPropertyChanged(nameof(IsOffline));
        OnPropertyChanged(nameof(IsOnline));
        OnPropertyChanged(nameof(Text));
    }
}
