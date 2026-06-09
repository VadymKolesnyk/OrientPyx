using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using OrientDesk.BusinessLogic.Interfaces;
using OrientDesk.Localization;
using OrientDesk.Presentation.Services;
using OrientDesk.Presentation.ViewModels.Dialogs;

namespace OrientDesk.Presentation.ViewModels.Pages;

/// <summary>
/// Spreadsheet-like control-point (КП) reference for the CURRENT competition day. Cells
/// auto-save in the background (debounced per row) — there is no Save button and no global
/// busy overlay, so editing never blocks the UI. Opened from the "Competition" top menu.
/// </summary>
public sealed partial class ControlPointsViewModel : PageViewModelBase
{
    private static readonly TimeSpan SaveDebounce = TimeSpan.FromMilliseconds(600);

    private readonly ICompetitionEditorService _editor;
    private readonly ISessionService _session;
    private readonly IIofXmlParser _xmlParser;
    private readonly IDialogService _dialogs;
    private readonly IBusyService _busy;
    private readonly Dictionary<Guid, CancellationTokenSource> _saveTimers = new();

    public ControlPointsViewModel(
        ILocalizationService localization,
        ICompetitionEditorService editor,
        ISessionService session,
        IIofXmlParser xmlParser,
        IDialogService dialogs,
        IBusyService busy)
        : base(localization)
    {
        _editor = editor;
        _session = session;
        _xmlParser = xmlParser;
        _dialogs = dialogs;
        _busy = busy;
        // Singleton VM: when the competition/day changes, drop the previous event's rows so the
        // page never shows stale data before it is next opened.
        _session.SessionChanged += (_, _) => _ = LoadAsync();
    }

    public override string NavKey => "Nav.ControlPoints";
    public override string TitleKey => "Page.ControlPoints.Title";
    public override string TextKey => "Page.ControlPoints.Text";

    public ObservableCollection<ControlPointRowViewModel> Points { get; } = [];

    /// <summary>True when a day is selected; otherwise the page shows a "pick a day" hint.</summary>
    public bool HasDay => _session.CurrentDay is not null;

    /// <summary>Reloads the control points for the current day. Called when the page is shown.</summary>
    public async Task LoadAsync()
    {
        CancelAllTimers();
        Points.Clear();
        OnPropertyChanged(nameof(HasDay));

        if (!HasDay)
            return;

        var points = await _editor.GetControlPointsAsync();
        foreach (var point in points)
            Points.Add(new ControlPointRowViewModel(point, Localization, RequestRowSave));
    }

    [RelayCommand]
    private async Task AddPointAsync()
    {
        if (!HasDay)
            return;

        // Persist immediately so the new row carries its real id for later debounced updates.
        var entity = await _editor.AddControlPointAsync();
        Points.Add(new ControlPointRowViewModel(entity, Localization, RequestRowSave));
    }

    /// <summary>Stable key for the single toggle on the control-points import modal.</summary>
    private const string ReplaceAllOptionKey = "replaceAll";

    /// <summary>
    /// Imports control points from IOF XML text (chosen by the view's file picker). Shows the
    /// shared import-options modal first: its one toggle, "replace all" (default on), decides
    /// whether the day's points are fully replaced or only new codes are appended. The actual
    /// parse + write run under the global busy overlay. Returns silently if no day is selected.
    /// </summary>
    public async Task ImportFromXmlAsync(string xml)
    {
        if (!HasDay || string.IsNullOrWhiteSpace(xml))
            return;

        // Parse up front so a malformed file is reported before the user sees the options modal.
        IofCourseDataParseOutcome outcome;
        try
        {
            var data = _xmlParser.Parse(xml);
            outcome = IofCourseDataParseOutcome.Ok(data);
        }
        catch (IofXmlFormatException ex)
        {
            outcome = IofCourseDataParseOutcome.Failed(ex.Message);
        }

        if (!outcome.Success)
        {
            await _dialogs.ShowImportOptionsAsync(new ImportOptionsViewModel(
                Localization,
                titleKey: "ControlPoints.Import.Title",
                messageKey: "ControlPoints.Import.Error",
                options: []));
            return;
        }

        var dialog = new ImportOptionsViewModel(
            Localization,
            titleKey: "ControlPoints.Import.Title",
            messageKey: "ControlPoints.Import.Message",
            options: [new ImportOption(ReplaceAllOptionKey, "ControlPoints.Import.ReplaceAll", isChecked: true)]);

        var result = await _dialogs.ShowImportOptionsAsync(dialog);
        if (result is null)
            return; // cancelled

        var replaceAll = result.Get(ReplaceAllOptionKey, fallback: true);
        await _busy.RunAsync(async () =>
        {
            await _editor.ImportControlPointsAsync(outcome.Data!, replaceAll);
            await LoadAsync();
        });
    }

    // Small local outcome wrapper so a parse failure can be surfaced through the same modal path.
    private readonly struct IofCourseDataParseOutcome
    {
        private IofCourseDataParseOutcome(bool success, BusinessLogic.Models.IofCourseData? data, string? error)
        {
            Success = success;
            Data = data;
            Error = error;
        }

        public bool Success { get; }
        public BusinessLogic.Models.IofCourseData? Data { get; }
        public string? Error { get; }

        public static IofCourseDataParseOutcome Ok(BusinessLogic.Models.IofCourseData data) => new(true, data, null);
        public static IofCourseDataParseOutcome Failed(string error) => new(false, null, error);
    }

    [RelayCommand]
    private async Task DeletePointAsync(ControlPointRowViewModel? row)
    {
        if (row is null)
            return;

        if (_saveTimers.TryGetValue(row.Id, out var cts))
        {
            cts.Cancel();
            _saveTimers.Remove(row.Id);
        }

        await _editor.DeleteControlPointAsync(row.Id);
        Points.Remove(row);
    }

    // Invoked by a row on every edit (UI thread). Resets that row's debounce timer.
    private void RequestRowSave(ControlPointRowViewModel row)
    {
        if (_saveTimers.TryGetValue(row.Id, out var existing))
            existing.Cancel();

        var cts = new CancellationTokenSource();
        _saveTimers[row.Id] = cts;
        _ = SaveRowDebouncedAsync(row, cts.Token); // fire-and-forget; the UI is never blocked
    }

    private async Task SaveRowDebouncedAsync(ControlPointRowViewModel row, CancellationToken token)
    {
        try
        {
            await Task.Delay(SaveDebounce, token);
            // ToEntity() is read after the delay, so the latest typed values are persisted.
            await _editor.UpdateControlPointAsync(row.ToEntity(), token);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer edit (or the page reloaded) — ignore.
        }
        catch
        {
            // Background save failed; never crash the UI over an autosave.
        }
    }

    private void CancelAllTimers()
    {
        foreach (var cts in _saveTimers.Values)
            cts.Cancel();
        _saveTimers.Clear();
    }
}
