using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.Localization;
using OrientPyx.Presentation.Services;
using OrientPyx.Presentation.ViewModels.Dialogs;
using OrientPyx.Presentation.ViewModels.Shared;

namespace OrientPyx.Presentation.ViewModels.Pages;

/// <summary>
/// A switched-on plugin's page in the «Плагіни» menu: an auto-built form over the settings of each of the plugin's
/// disciplines (Р-11), for the open competition. Day-scoped values belong to the day picked here (the session day by
/// default; choosing one never changes the session), competition-scoped ones to every day. One singleton VM serves
/// every plugin — the root coordinator sets <see cref="PluginId"/> before loading, like the start-protocol kind.
/// </summary>
public sealed partial class PluginSettingsViewModel : PageViewModelBase
{
    private readonly IPluginCatalog _plugins;
    private readonly IDisciplineStrategyProvider _strategies;
    private readonly ICompetitionEditorService _editor;
    private readonly ISessionService _session;
    private readonly IBusyService _busy;
    private readonly IDialogService _dialogs;

    // Guards SelectedDay while LoadAsync fills the picker, so the setter does not reload twice.
    private bool _syncingDay;

    public PluginSettingsViewModel(
        ILocalizationService localization, IPluginCatalog plugins, IDisciplineStrategyProvider strategies,
        ICompetitionEditorService editor, ISessionService session, IBusyService busy, IDialogService dialogs)
        : base(localization)
    {
        _plugins = plugins;
        _strategies = strategies;
        _editor = editor;
        _session = session;
        _busy = busy;
        _dialogs = dialogs;

        // Singleton VM: reload on a competition/day change (marshal to UI) — but only once a plugin was opened.
        _session.SessionChanged += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (PluginId.Length > 0)
                _ = LoadAsync(resetSaved: false);
        });
    }

    public override string NavKey => "Nav.PluginSettings";
    public override string TitleKey => "Page.PluginSettings.Title";
    public override string TextKey => "Page.PluginSettings.Text";

    public override string HelpKeyPrefix => "Help.PluginSettings";

    /// <summary>The plugin shown; set by the root coordinator before <see cref="LoadAsync"/>.</summary>
    public string PluginId { get; set; } = string.Empty;

    [ObservableProperty]
    private string _pluginName = string.Empty;

    [ObservableProperty]
    private string _pluginDetails = string.Empty;

    // ── Day picker (does NOT touch the session)

    public ObservableCollection<DayOption> DayOptions { get; } = [];

    [ObservableProperty]
    private DayOption? _selectedDay;

    /// <summary>The picker only matters when some setting is per day and there is more than one day.</summary>
    public bool ShowDaySelector => DayOptions.Count > 1 && HasDaySettings;

    // ── Form

    public ObservableCollection<DisciplineSettingsSection> Sections { get; } = [];

    public bool HasSections => Sections.Count > 0;

    /// <summary>Some setting is stored per day (so the picked day matters).</summary>
    public bool HasDaySettings => Sections.Any(s => s.Items.Any(i => !i.HasScopeNote));

    /// <summary>Shown after a save until the form is reloaded.</summary>
    [ObservableProperty]
    private bool _saved;

    /// <param name="resetSaved">False for the reload a save itself triggers, so its «Збережено» stays shown.</param>
    public async Task LoadAsync(bool resetSaved = true)
    {
        if (resetSaved)
            Saved = false;
        var plugin = _plugins.Plugins.FirstOrDefault(p => p.Info.Id == PluginId);
        PluginName = plugin?.Info.Name ?? PluginId;
        PluginDetails = plugin is null
            ? string.Empty
            : string.Join(" · ", new[] { plugin.Info.Version, plugin.Info.Author, plugin.Info.Description }
                .Where(s => !string.IsNullOrWhiteSpace(s)));

        var days = _session.CurrentEvent is null ? [] : await _busy.RunAsync(() => _editor.GetDaysAsync());
        _syncingDay = true;
        try
        {
            var keep = SelectedDay?.Number ?? _session.CurrentDay?.Number;
            DayOptions.Clear();
            foreach (var day in days)
                DayOptions.Add(new DayOption(day, Localization));
            SelectedDay = DayOptions.FirstOrDefault(o => o.Number == keep) ?? DayOptions.FirstOrDefault();
        }
        finally
        {
            _syncingDay = false;
        }

        await LoadSectionsAsync();
    }

    partial void OnSelectedDayChanged(DayOption? value)
    {
        if (_syncingDay)
            return;
        Saved = false;
        _ = LoadSectionsAsync();
    }

    private async Task LoadSectionsAsync()
    {
        var plugin = _plugins.Plugins.FirstOrDefault(p => p.Info.Id == PluginId);
        var sections = new List<DisciplineSettingsSection>();
        if (plugin is not null && _plugins.IsEnabled(PluginId) && SelectedDay?.Day is { } day)
        {
            var loaded = await _busy.RunAsync(async () =>
            {
                var used = await _editor.GetDayDisciplinesAsync(day.Id);
                var list = new List<(IDisciplineStrategy Strategy, BusinessLogic.Models.DisciplineSettingsData Data, bool Used)>();
                foreach (var discipline in plugin.Disciplines)
                {
                    var strategy = _strategies.For(new DisciplineId(discipline.Id));
                    if (strategy.Settings.Count == 0)
                        continue;
                    if (await _editor.GetDisciplineSettingsAsync(day.Id, strategy.Id) is { } data)
                        list.Add((strategy, data, used.Contains(strategy.Id)));
                }
                return list;
            });

            foreach (var (strategy, data, used) in loaded)
                sections.Add(new DisciplineSettingsSection(
                    strategy.Id,
                    DisciplineLabels.Of(strategy, Localization),
                    used ? string.Empty : Localization.Get("Page.PluginSettings.NotUsedOnDay"),
                    data.Definitions.Select(def => new SettingItemViewModel(Localization, data.PluginId, def,
                        data.Values.GetValueOrDefault(def.Key), EditTableAsync)).ToList()));
        }

        Sections.Clear();
        foreach (var section in sections)
            Sections.Add(section);
        OnPropertyChanged(nameof(HasSections));
        OnPropertyChanged(nameof(HasDaySettings));
        OnPropertyChanged(nameof(ShowDaySelector));
    }

    private Task<TableValue?> EditTableAsync(
        string? pluginId, SettingDef def, TableValue value) =>
        _dialogs.ShowSettingTableAsync(new SettingTableViewModel(Localization, pluginId, def, value));

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (SelectedDay?.Day is not { } day || Sections.Count == 0)
            return;

        var edited = Sections.ToDictionary(
            s => s.DisciplineId,
            s => (IReadOnlyDictionary<string, object?>)s.Items.ToDictionary(i => i.Key, i => i.Value));
        await _busy.RunAsync(async () =>
        {
            foreach (var (id, values) in edited)
                await _editor.SaveDisciplineSettingsAsync(day.Id, id, values);
        });

        // Re-point the session at the active day so every page reloads and recomputes with the new values (results
        // are computed on read); that reloads this page too.
        if (_session.CurrentDay is { } current)
            await _busy.RunAsync(() => _session.SetCurrentDayAsync(current));
        Saved = true;
    }
}
