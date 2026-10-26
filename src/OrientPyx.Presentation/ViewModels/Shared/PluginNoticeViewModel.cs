using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.BusinessLogic.Disciplines.Plugins;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.Localization;
using OrientPyx.Presentation.ViewModels.Pages;

namespace OrientPyx.Presentation.ViewModels.Shared;

/// <summary>
/// The plugin banners above every page: «Потрібен плагін» while the active day uses a discipline whose plugin is
/// missing or disabled (Р-2), and a one-per-session notice when plugin code failed and the built-in rules were used
/// instead (Р-3).
/// </summary>
public sealed partial class PluginNoticeViewModel : ObservableObject
{
    private readonly ISessionService _session;
    private readonly ICompetitionEditorService _editor;
    private readonly IDisciplineStrategyProvider _strategies;
    private IReadOnlyList<DisciplineId> _missing = [];
    private (string PluginId, DisciplineId Fallback)? _failure;

    public PluginNoticeViewModel(
        ILocalizationService localization, ISessionService session, ICompetitionEditorService editor,
        IDisciplineStrategyProvider strategies, PluginRuntimeErrors errors)
    {
        Localization = localization;
        _session = session;
        _editor = editor;
        _strategies = strategies;
        _session.SessionChanged += (_, _) => Dispatcher.UIThread.Post(() => _ = RefreshAsync());
        errors.FirstFailure += (pluginId, _, fallback) =>
            Dispatcher.UIThread.Post(() =>
            {
                _failure = (pluginId, fallback);
                RaiseTexts();
            });
        Localization.PropertyChanged += (_, _) => RaiseTexts();
    }

    public ILocalizationService Localization { get; }

    public string MissingText => _missing.Count == 0
        ? string.Empty
        : string.Format(Localization.Get("Banner.MissingPlugin"),
            string.Join(", ", _missing.Select(id => id.Value)),
            string.Join(", ", _missing.Select(id => id.Value.Split('.')[0]).Distinct()));

    public bool HasMissing => _missing.Count > 0;

    public string FailureText => _failure is { } f
        ? string.Format(Localization.Get("Banner.PluginFailed"), f.PluginId,
            DisciplineLabels.Of(_strategies.For(f.Fallback), Localization))
        : string.Empty;

    public bool HasFailure => _failure is not null;

    [RelayCommand]
    private void DismissFailure()
    {
        _failure = null;
        RaiseTexts();
    }

    /// <summary>Re-checks the active day's disciplines (also after a day's or group's discipline was edited).</summary>
    public async Task RefreshAsync()
    {
        IReadOnlyList<DisciplineId> missing = [];
        if (_session.CurrentDay is { } day)
        {
            try
            {
                var used = await Task.Run(() => _editor.GetDayDisciplinesAsync(day.Id));
                missing = used.Where(id => _strategies.For(id).IsUnknown).ToList();
            }
            catch (Exception)
            {
                // A banner is best-effort; the pages report real failures themselves.
            }
        }
        _missing = missing;
        RaiseTexts();
    }

    private void RaiseTexts()
    {
        OnPropertyChanged(nameof(MissingText));
        OnPropertyChanged(nameof(HasMissing));
        OnPropertyChanged(nameof(FailureText));
        OnPropertyChanged(nameof(HasFailure));
    }
}
