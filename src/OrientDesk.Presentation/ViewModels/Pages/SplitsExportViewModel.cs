using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using OrientDesk.BusinessLogic.Entities;
using OrientDesk.BusinessLogic.Interfaces;
using OrientDesk.BusinessLogic.Models;
using OrientDesk.Localization;
using OrientDesk.Presentation.Services;

namespace OrientDesk.Presentation.ViewModels.Pages;

/// <summary>
/// «Спліти (HTML)»: builds and exports a day's splits to a modern, self-contained UTF-8 HTML file. For a
/// set-course day the export is a per-control split table; for a free-order / scored (rogaine) day it is a
/// per-runner passage. The header text fields fall back to the current competition's metadata when left
/// blank. Choosing a day here never changes the active session day (an export is read-only over a day).
/// Mirrors <see cref="ProtocolsViewModel"/> but without the column/orientation settings — HTML lays itself out.
/// </summary>
public sealed partial class SplitsExportViewModel : PageViewModelBase
{
    private readonly ICompetitionEditorService _editor;
    private readonly ISessionService _session;
    private readonly ISplitExportBuilder _builder;
    private readonly ISplitHtmlWriter _writer;
    private readonly IBusyService _busy;

    // Guards SelectedDay sync during LoadAsync so the setter doesn't fight the load.
    private bool _syncingDay;

    public SplitsExportViewModel(
        ILocalizationService localization,
        ICompetitionEditorService editor,
        ISessionService session,
        ISplitExportBuilder builder,
        ISplitHtmlWriter writer,
        IBusyService busy)
        : base(localization)
    {
        _editor = editor;
        _session = session;
        _builder = builder;
        _writer = writer;
        _busy = busy;

        // Singleton VM: reload the day list + header defaults on a competition/day change (marshal to UI).
        _session.SessionChanged += (_, _) => Dispatcher.UIThread.Post(() => _ = LoadAsync());
    }

    public override string NavKey => "Nav.Splits";
    public override string TitleKey => "Page.Splits.Title";
    public override string TextKey => "Page.Splits.Text";

    public override string IconData =>
        "M4,18 h4 v-6 h-4 z M10,18 h4 v-12 h-4 z M16,18 h4 v-9 h-4 z";

    // ── Day picker (does NOT touch the session) ──────────────────────────────────────────────────────

    public ObservableCollection<DayOption> DayOptions { get; } = [];

    [ObservableProperty]
    private DayOption? _selectedDay;

    public bool ShowDaySelector => DayOptions.Count > 1;

    // ── Header text ──────────────────────────────────────────────────────────────────────────────────

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _subtitle = string.Empty;

    [ObservableProperty]
    private string _competitionType = string.Empty;

    [ObservableProperty]
    private string _venue = string.Empty;

    [ObservableProperty]
    private string _dateText = string.Empty;

    public async Task LoadAsync()
    {
        var (days, info) = await _busy.RunAsync(async () =>
        {
            var d = await _editor.GetDaysAsync();
            var i = await _editor.GetInfoAsync();
            return (d, i);
        });

        _syncingDay = true;
        try
        {
            DayOptions.Clear();
            foreach (var day in days)
                DayOptions.Add(new DayOption(day, Localization));

            var current = _session.CurrentDay?.Number;
            SelectedDay = DayOptions.FirstOrDefault(o => o.Number == current) ?? DayOptions.FirstOrDefault();
        }
        finally
        {
            _syncingDay = false;
        }
        OnPropertyChanged(nameof(ShowDaySelector));

        SeedHeaderDefaults(info);
    }

    // Fills blank header fields with the competition's own values (and the selected day's date), so the
    // export always has a header without the user typing anything.
    private void SeedHeaderDefaults(CompetitionInfo? info)
    {
        if (string.IsNullOrWhiteSpace(Title) && !string.IsNullOrWhiteSpace(info?.Name))
            Title = info!.Name;
        if (string.IsNullOrWhiteSpace(Subtitle) && !string.IsNullOrWhiteSpace(info?.Organisation))
            Subtitle = info!.Organisation;
        if (string.IsNullOrWhiteSpace(Venue) && !string.IsNullOrWhiteSpace(info?.Venue))
            Venue = info!.Venue;
        if (string.IsNullOrWhiteSpace(DateText) && SelectedDay?.Day?.Date is { } date)
            DateText = date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
    }

    partial void OnSelectedDayChanged(DayOption? value)
    {
        // A day change here only repoints the export target; it must NOT switch the session day. Refresh the
        // default date to the newly chosen day when the date field is still empty.
        if (_syncingDay)
            return;
        if (string.IsNullOrWhiteSpace(DateText) && value?.Day?.Date is { } date)
            DateText = date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Builds the split HTML for the selected day and returns the bytes + a suggested file name, or null when
    /// there is nothing to export (no competition / no day). The View runs the save dialog.
    /// </summary>
    public async Task<SplitsExportResult?> GenerateAsync()
    {
        if (_session.CurrentEvent is null || SelectedDay?.Day is not { } day)
            return null;

        var header = new SplitExportHeader(
            Title?.Trim() ?? string.Empty,
            Subtitle?.Trim() ?? string.Empty,
            CompetitionType?.Trim() ?? string.Empty,
            Venue?.Trim() ?? string.Empty,
            DateText?.Trim() ?? string.Empty);
        var labels = BuildLabels();

        var bytes = await _busy.RunAsync(async () =>
        {
            var data = await _editor.GetDaySplitsExportDataAsync(day.Id);
            var document = _builder.Build(data, header, labels);
            return _writer.Write(document);
        });

        return new SplitsExportResult(bytes, SuggestedFileName(day));
    }

    private SplitExportLabels BuildLabels() => new(
        DefaultTitle: Localization.Get("Splits.DefaultTitle"),
        ColumnPlace: Localization.Get("Splits.Col.Place"),
        ColumnName: Localization.Get("Splits.Col.Name"),
        ColumnNumber: Localization.Get("Splits.Col.Number"),
        ColumnResult: Localization.Get("Splits.Col.Result"),
        ColumnFinish: Localization.Get("Splits.Col.Finish"),
        ColumnScore: Localization.Get("Splits.Col.Score"),
        ControlPrefix: Localization.Get("Splits.Col.ControlPrefix"),
        DistanceLabel: Localization.Get("Splits.Section.Distance"),
        ColumnDistance: Localization.Get("Splits.Col.Distance"),
        ControlCountLabel: Localization.Get("Splits.Section.ControlCount"),
        GeneratedLabel: Localization.Get("Splits.Generated"),
        // Reuse the participant tables' «Бали» tooltip strings, so the export cell title reads identically.
        ScoreTooltipHeader: Localization.Get("Participants.Score.Tooltip.Header"),
        ScoreTooltipControl: Localization.Get("Participants.Score.Tooltip.Control"),
        ScoreTooltipGross: Localization.Get("Participants.Score.Tooltip.Gross"),
        ScoreTooltipPenalty: Localization.Get("Participants.Score.Tooltip.Penalty"),
        ScoreTooltipBonus: Localization.Get("Participants.Score.Tooltip.Bonus"),
        ScoreTooltipTotal: Localization.Get("Participants.Score.Tooltip.Total"),
        SplitLossTotal: Localization.Get("Splits.Loss.Total"),
        SplitLossLeg: Localization.Get("Splits.Loss.Leg"));

    // "<competition> — спліти <День N> <date>.html", sanitised for the save dialog.
    private string SuggestedFileName(EventDay day)
    {
        var competition = _session.CurrentEvent?.Name;
        if (string.IsNullOrWhiteSpace(competition))
            competition = Localization.Get("Splits.DefaultName");
        var part = Localization.Get("Splits.NamePart");
        var stamp = DateTime.Now.ToString("yyyy-MM-dd");
        var baseName = $"{competition} — {part} {Localization.Get("Header.Day")} {day.Number} {stamp}";
        foreach (var invalid in System.IO.Path.GetInvalidFileNameChars())
            baseName = baseName.Replace(invalid, '_');
        return $"{baseName}.html";
    }
}

/// <summary>The result of building a split export: the HTML bytes and a suggested save file name.</summary>
public sealed record SplitsExportResult(byte[] Bytes, string SuggestedFileName);
