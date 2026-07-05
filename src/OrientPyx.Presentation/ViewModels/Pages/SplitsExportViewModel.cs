using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Localization;
using OrientPyx.Presentation.Services;

namespace OrientPyx.Presentation.ViewModels.Pages;

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

    // Lucide "chart-column" (bars).
    public override string IconData =>
        "M4 20V10 M10 20V4 M16 20v-6 M4 20h18";

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

    // ── Header placeholders (watermarks) ───────────────────────────────────────────────────────────────
    // The resolved competition/day default for each header field, shown as the TextBox watermark when the
    // user has typed nothing. A blank field falls back to this value at build/export time (so the exported
    // split sheet carries the competition's own metadata); when the placeholder is itself empty (the DB has
    // no value) the field stays blank everywhere — the watermark is then a hint label, not inserted text.
    // Mirrors <see cref="ProtocolsViewModel"/>.

    [ObservableProperty]
    private string _titlePlaceholder = string.Empty;

    [ObservableProperty]
    private string _subtitlePlaceholder = string.Empty;

    [ObservableProperty]
    private string _venuePlaceholder = string.Empty;

    [ObservableProperty]
    private string _competitionTypePlaceholder = string.Empty;

    [ObservableProperty]
    private string _dateTextPlaceholder = string.Empty;

    // The current competition's metadata, used to resolve the header placeholders on each day load.
    private CompetitionInfo? _competitionInfo;

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

        _competitionInfo = info;
        ResolveHeaderPlaceholders(info);
    }

    // Resolves the header watermark for each blank-able field. The per-day fields (date, venue) come from the
    // SELECTED DAY first and fall back to the competition; the rest from the competition. These are shown as
    // the TextBox placeholders and used as the build-time fallback for a field the user left blank — never
    // written into the editable field, so an untouched field stays empty and a missing DB value yields an
    // empty header cell, not the placeholder hint. The split export's «Title» is the competition name and
    // «Subtitle» the organising body (the split sheet has no separate competition-name line).
    private void ResolveHeaderPlaceholders(CompetitionInfo? info)
    {
        var day = SelectedDay?.Day;

        var name = !string.IsNullOrWhiteSpace(info?.Name) ? info!.Name : _session.CurrentEvent?.Name;
        TitlePlaceholder = name?.Trim() ?? Localization.Get("Splits.DefaultTitle");
        SubtitlePlaceholder = info?.Organisation?.Trim() ?? string.Empty;

        // Venue: the day's own venue, else the competition venue.
        VenuePlaceholder = FirstNonBlank(day?.Venue, info?.Venue);

        // Date: the day's date, else the competition's start date.
        var date = day?.Date ?? info?.StartDate;
        DateTextPlaceholder = date is { } d ? d.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) : string.Empty;

        // Competition type ("тип дистанції"): the day's default discipline, localized. No competition-level
        // equivalent, so blank when no day is selected.
        CompetitionTypePlaceholder = day is { } ? Localization.Get("Discipline.Type." + day.DefaultDiscipline) : string.Empty;
    }

    // The first of the candidates that is non-blank (trimmed), or empty when none is.
    private static string FirstNonBlank(params string?[] candidates)
    {
        foreach (var c in candidates)
            if (!string.IsNullOrWhiteSpace(c))
                return c!.Trim();
        return string.Empty;
    }

    partial void OnSelectedDayChanged(DayOption? value)
    {
        // A day change here only repoints the export target; it must NOT switch the session day. Refresh the
        // header placeholders to the newly chosen day's defaults (the editable fields are untouched).
        if (_syncingDay)
            return;
        ResolveHeaderPlaceholders(_competitionInfo);
    }

    /// <summary>
    /// Builds the split HTML for the selected day and returns the bytes + a suggested file name, or null when
    /// there is nothing to export (no competition / no day). The View runs the save dialog.
    /// </summary>
    public async Task<SplitsExportResult?> GenerateAsync()
    {
        if (_session.CurrentEvent is null || SelectedDay?.Day is not { } day)
            return null;

        // Each blank field falls back to its resolved placeholder (the competition/day default), so the
        // exported header carries the competition's own metadata without the user retyping it; a placeholder
        // that is itself empty leaves the field blank (an empty header line). The builder applies its own
        // localized default title when the title resolves to empty.
        var header = new SplitExportHeader(
            FoldPlaceholder(Title, TitlePlaceholder),
            FoldPlaceholder(Subtitle, SubtitlePlaceholder),
            FoldPlaceholder(CompetitionType, CompetitionTypePlaceholder),
            FoldPlaceholder(Venue, VenuePlaceholder),
            FoldPlaceholder(DateText, DateTextPlaceholder));
        var labels = BuildLabels();

        var bytes = await _busy.RunAsync(async () =>
        {
            var data = await _editor.GetDaySplitsExportDataAsync(day.Id);
            var document = _builder.Build(data, header, labels);
            return _writer.Write(document);
        });

        return new SplitsExportResult(bytes, SuggestedFileName(day));
    }

    // The user's typed value (trimmed), or the resolved placeholder when the field was left blank.
    private static string FoldPlaceholder(string? value, string placeholder)
    {
        var v = value?.Trim() ?? string.Empty;
        return v.Length > 0 ? v : placeholder;
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
