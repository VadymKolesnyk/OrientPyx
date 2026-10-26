using CommunityToolkit.Mvvm.ComponentModel;

namespace OrientPyx.Presentation.ViewModels.Pages;

/// <summary>One row of the «Плагіни» settings card: a loaded plugin (with its on/off switch) or one that failed to
/// load (with the reason). Switching a plugin on or off takes effect after a restart.</summary>
public sealed partial class PluginItemViewModel : ObservableObject
{
    private readonly Action<PluginItemViewModel>? _onToggled;

    public PluginItemViewModel(
        string id, string name, string details, string folder, string status, bool isError, bool enabled,
        Action<PluginItemViewModel>? onToggled)
    {
        Id = id;
        Name = name;
        Details = details;
        Folder = folder;
        Status = status;
        IsError = isError;
        _enabled = enabled;
        _onToggled = onToggled;
    }

    public string Id { get; }

    public string Name { get; }

    /// <summary>Version and author, e.g. "1.0 · OrientPyx".</summary>
    public string Details { get; }

    public string Folder { get; }

    /// <summary>The load state or the error message.</summary>
    public string Status { get; }

    public bool IsError { get; }

    /// <summary>A loaded plugin can be switched on or off; a failed one cannot.</summary>
    public bool CanToggle => !IsError;

    [ObservableProperty]
    private bool _enabled;

    partial void OnEnabledChanged(bool value) => _onToggled?.Invoke(this);
}
