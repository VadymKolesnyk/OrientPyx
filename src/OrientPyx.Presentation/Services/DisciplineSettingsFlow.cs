using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.Localization;
using OrientPyx.Presentation.ViewModels.Dialogs;
using OrientPyx.Presentation.ViewModels.Pages;

namespace OrientPyx.Presentation.Services;

/// <summary>
/// «Налаштування дисципліни…» from the days and groups pages: edits the settings of every discipline used on a day
/// that declares any, then refreshes the pages when it is the active day (results are computed on read, Р-11).
/// </summary>
public interface IDisciplineSettingsFlow
{
    /// <summary>True when any of <paramref name="disciplines"/> declares settings (the button is shown).</summary>
    bool HasSettings(IEnumerable<DisciplineId> disciplines);

    Task EditAsync(EventDay day);
}

public sealed class DisciplineSettingsFlow : IDisciplineSettingsFlow
{
    private readonly ICompetitionEditorService _editor;
    private readonly IDisciplineStrategyProvider _strategies;
    private readonly IDialogService _dialogs;
    private readonly IBusyService _busy;
    private readonly ISessionService _session;
    private readonly ILocalizationService _localization;

    public DisciplineSettingsFlow(
        ICompetitionEditorService editor, IDisciplineStrategyProvider strategies, IDialogService dialogs,
        IBusyService busy, ISessionService session, ILocalizationService localization)
    {
        _editor = editor;
        _strategies = strategies;
        _dialogs = dialogs;
        _busy = busy;
        _session = session;
        _localization = localization;
    }

    public bool HasSettings(IEnumerable<DisciplineId> disciplines) =>
        disciplines.Any(id => _strategies.For(id).Settings.Count > 0);

    public async Task EditAsync(EventDay day)
    {
        var sections = await _busy.RunAsync(async () =>
        {
            var list = new List<(string, BusinessLogic.Models.DisciplineSettingsData)>();
            foreach (var id in await _editor.GetDayDisciplinesAsync(day.Id))
            {
                var strategy = _strategies.For(id);
                if (strategy.Settings.Count == 0)
                    continue;
                if (await _editor.GetDisciplineSettingsAsync(day.Id, id) is { } data)
                    list.Add((DisciplineLabels.Of(strategy, _localization), data));
            }
            return list;
        });
        if (sections.Count == 0)
            return;

        var dialog = new DisciplineSettingsViewModel(
            _localization, string.Format(_localization.Get("Dashboard.Today.Day"), day.Number), sections,
            (pluginId, def, value) => _dialogs.ShowSettingTableAsync(new SettingTableViewModel(_localization, pluginId, def, value)));
        var edited = await _dialogs.ShowDisciplineSettingsAsync(dialog);
        if (edited is null)
            return;

        await _busy.RunAsync(async () =>
        {
            foreach (var (id, values) in edited)
                await _editor.SaveDisciplineSettingsAsync(day.Id, id, values);
        });

        // Re-point the session at the active day so every page reloads and recomputes with the new values.
        if (_session.CurrentDay is { } current)
            await _busy.RunAsync(() => _session.SetCurrentDayAsync(current));
    }
}
