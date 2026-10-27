using CommunityToolkit.Mvvm.ComponentModel;

namespace OrientPyx.Presentation.ViewModels.Pages;

/// <summary>One row of the «Плагіни» page: a loaded plugin (with its on/off switch and, while on, the way to its
/// settings page) or one that failed to load (with the reason). Switching takes effect at once.</summary>
public sealed partial class PluginItemViewModel : ObservableObject
{
    private readonly Action<PluginItemViewModel>? _onToggled;

    public PluginItemViewModel(
        string id, string name, string details, string description, string folder, string error, bool enabled,
        Action<PluginItemViewModel>? onToggled)
    {
        Id = id;
        Name = name;
        Details = details;
        Description = description;
        Folder = folder;
        Error = error;
        _enabled = enabled;
        _onToggled = onToggled;
    }

    public string Id { get; }

    public string Name { get; }

    /// <summary>Version and author, e.g. "1.0 · OrientPyx".</summary>
    public string Details { get; }

    public string Description { get; }

    public bool HasDescription => Description.Length > 0;

    public string Folder { get; }

    /// <summary>Why the plugin could not be loaded; empty for a loaded one.</summary>
    public string Error { get; }

    public bool IsError => Error.Length > 0;

    /// <summary>A loaded plugin can be switched on or off; a failed one cannot.</summary>
    public bool CanToggle => !IsError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOpenSettings))]
    private bool _enabled;

    /// <summary>A switched-on plugin has its own page (and «Плагіни» menu item).</summary>
    public bool CanOpenSettings => Enabled && !IsError;

    partial void OnEnabledChanged(bool value) => _onToggled?.Invoke(this);
}
