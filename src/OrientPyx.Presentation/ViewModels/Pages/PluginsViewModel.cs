using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.Localization;
using OrientPyx.Presentation.Services;

namespace OrientPyx.Presentation.ViewModels.Pages;

/// <summary>
/// «Плагіни → Керування плагінами»: every plugin found in the plugin folders, each off until switched on here. A
/// switched-on plugin's disciplines appear in the type dropdowns at once, and it gets its own settings page in the
/// «Плагіни» menu (<see cref="PluginSettingsViewModel"/>). The choice is app-level (the app database).
/// </summary>
public sealed partial class PluginsViewModel : PageViewModelBase
{
    private readonly IPluginCatalog _plugins;
    private readonly IBusyService _busy;
    private readonly ISessionService _session;

    // Set while the rows are rebuilt, so restoring a check box does not count as a click.
    private bool _loading;

    public PluginsViewModel(ILocalizationService localization, IPluginCatalog plugins, IBusyService busy, ISessionService session)
        : base(localization)
    {
        _plugins = plugins;
        _busy = busy;
        _session = session;
        Localization.PropertyChanged += (_, _) => Load();
        Load();
    }

    public override string NavKey => "Nav.Plugins";
    public override string TitleKey => "Page.Plugins.Title";
    public override string TextKey => "Page.Plugins.Text";

    // Lucide "puzzle".
    public override string IconData =>
        "M15.39 4.39a1 1 0 0 0 1.68-.474 2.5 2.5 0 1 1 3.014 3.015 1 1 0 0 0-.474 1.68l1.683 1.682a2.414 2.414 0 0 1 0 3.414L19.61 15.39a1 1 0 0 1-1.68-.474 2.5 2.5 0 1 0-3.014 3.015 1 1 0 0 1 .474 1.68l-1.683 1.682a2.414 2.414 0 0 1-3.414 0L8.61 19.61a1 1 0 0 0-1.68.474 2.5 2.5 0 1 1-3.014-3.015 1 1 0 0 0 .474-1.68l-1.683-1.682a2.414 2.414 0 0 1 0-3.414L4.39 8.61a1 1 0 0 1 1.68.474 2.5 2.5 0 1 0 3.014-3.015 1 1 0 0 1-.474-1.68l1.683-1.682a2.414 2.414 0 0 1 3.414 0z";

    public ObservableCollection<PluginItemViewModel> Plugins { get; } = [];

    public bool HasPlugins => Plugins.Count > 0;

    /// <summary>Raised by a row's «Налаштування» button with the plugin id; the root coordinator opens its page.</summary>
    public event EventHandler<string>? OpenSettingsRequested;

    public Task LoadAsync()
    {
        Load();
        return Task.CompletedTask;
    }

    private void Load()
    {
        _loading = true;
        try
        {
            Plugins.Clear();
            foreach (var p in _plugins.Plugins)
            {
                var details = string.IsNullOrWhiteSpace(p.Info.Author) ? p.Info.Version : $"{p.Info.Version} · {p.Info.Author}";
                Plugins.Add(new PluginItemViewModel(p.Info.Id, p.Info.Name, details, p.Info.Description ?? string.Empty,
                    p.Folder, error: string.Empty, _plugins.IsEnabled(p.Info.Id), OnPluginToggled));
            }
            foreach (var e in _plugins.Errors)
                Plugins.Add(new PluginItemViewModel(e.PluginId ?? string.Empty, e.PluginId ?? Path.GetFileName(e.Path),
                    string.Empty, string.Empty, e.Path, string.Format(Localization.Get("Page.Plugins.Error"), e.Message),
                    enabled: false, onToggled: null));
        }
        finally
        {
            _loading = false;
        }
        OnPropertyChanged(nameof(HasPlugins));
    }

    private async void OnPluginToggled(PluginItemViewModel item)
    {
        if (_loading)
            return;
        try
        {
            await _busy.RunAsync(() => _plugins.SetEnabledAsync(item.Id, item.Enabled));
            // Re-point the session so every page reloads: the type dropdowns, the «Потрібен плагін» banner and the
            // results all follow the plugin's new state.
            if (_session.CurrentDay is { } day)
                await _busy.RunAsync(() => _session.SetCurrentDayAsync(day));
        }
        catch (Exception)
        {
            // The busy service already logged and reported it.
        }
    }

    [RelayCommand]
    private void OpenSettings(PluginItemViewModel? item)
    {
        if (item is { CanOpenSettings: true })
            OpenSettingsRequested?.Invoke(this, item.Id);
    }

    [RelayCommand]
    private void OpenPluginsFolder()
    {
        var folder = _plugins.UserFolder;
        try
        {
            Directory.CreateDirectory(folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Opening Explorer is best-effort.
        }
    }
}
