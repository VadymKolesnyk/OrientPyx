using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Localization;
using OrientPyx.Presentation.Services;

namespace OrientPyx.Presentation.ViewModels.Pages;

/// <summary>
/// Drives the passage/splits panel under (or beside) the finish-read log. When a log row is selected the
/// page fills this from <c>ICompetitionEditorService.GetFinishSplitsAsync</c>; the panel is shown only
/// while <see cref="HasData"/> is true. The discipline decides the shape: an <see cref="IsOrdered"/>
/// layout (set course) shows two parallel lists — the actual passage (<see cref="Passage"/>, every punch
/// in chip order) beside the prescribed course (<see cref="Expected"/>) — while an <see cref="IsScored"/>
/// layout (score / choice / rogaine) lists the allowed controls with points and a running total. The
/// dock side (<see cref="IsDockedRight"/>) and panel size are user preferences (persisted app-wide).
/// </summary>
public sealed partial class FinishSplitsViewModel : ObservableObject
{
    private readonly ILocalizationService _localization;
    private readonly IUiPreferencesService _preferences;

    public FinishSplitsViewModel(ILocalizationService localization, IUiPreferencesService preferences)
    {
        _localization = localization;
        _preferences = preferences;
        _isDockedRight = preferences.SplitsDock == SplitsDock.Right;
        _panelSize = preferences.SplitsSize;
        _prescribedWidth = preferences.SplitsPrescribedWidth;
        // The right-list title is resolved text (not an indexer binding), so re-raise it on a language change.
        _localization.PropertyChanged += (_, _) => OnPropertyChanged(nameof(PrescribedTitle));
    }

    public ILocalizationService Localization => _localization;

    /// <summary>Subtitle line (selected participant + group), shown in the panel header.</summary>
    [ObservableProperty]
    private string _heading = string.Empty;

    /// <summary>Ordered layout — the actual passage (every punch in chip order); empty for the scored layout.</summary>
    public ObservableCollection<PassagePunchViewModel> Passage { get; } = [];

    /// <summary>Ordered layout — the prescribed course in order; empty for the scored layout.</summary>
    public ObservableCollection<ExpectedControlViewModel> Expected { get; } = [];

    /// <summary>Scored (score/choice/rogaine) layout rows; empty for the ordered layout.</summary>
    public ObservableCollection<ScoreEntryViewModel> Entries { get; } = [];

    [ObservableProperty]
    private bool _hasData;

    [ObservableProperty]
    private bool _isOrdered;

    [ObservableProperty]
    private bool _isScored;

    /// <summary>
    /// True when the ordered layout scores points (rogaine) — shows the Бали / Сума columns in the passage
    /// and course lists, and titles the right list «КП дистанції» instead of «Правильний порядок».
    /// </summary>
    [ObservableProperty]
    private bool _showPoints;

    /// <summary>Right-hand course list's title — «КП дистанції» when scored (rogaine), the set-course
    /// «Правильний порядок» otherwise. Re-resolved on a points-flag or language change.</summary>
    public string PrescribedTitle => _localization.Get(
        ShowPoints ? "FinishRead.Splits.Controls" : "FinishRead.Splits.Prescribed");

    partial void OnShowPointsChanged(bool value) => OnPropertyChanged(nameof(PrescribedTitle));

    /// <summary>Summary line: "visited / expected" and, for scored, the total points.</summary>
    [ObservableProperty]
    private string _summary = string.Empty;

    /// <summary>Detected scatter variant line («Розсіювання: A»), shown only for a scatter course; blank
    /// otherwise (drives its own visibility via <see cref="HasVariant"/>).</summary>
    [ObservableProperty]
    private string _variantText = string.Empty;

    /// <summary>True when a scatter variant was detected (the panel then shows <see cref="VariantText"/>).</summary>
    [ObservableProperty]
    private bool _hasVariant;

    // --- Dock side + size (persisted app-wide as preferences.json) ---------------------------------

    /// <summary>True when the panel is docked to the right of the table; false = below it.</summary>
    [ObservableProperty]
    private bool _isDockedRight;

    /// <summary>Panel size along its docked edge (height when bottom, width when right), in DIPs.</summary>
    [ObservableProperty]
    private double _panelSize;

    /// <summary>
    /// Width (DIPs) of the prescribed-course column in the ordered layout; the passage list takes the
    /// remaining space. The prescribed side is the splitter-sized (pixel) column — it must be the one the
    /// splitter resizes so the drag works in both directions (a pixel "next" column to a star "previous"
    /// one). Seeded onto the column in code-behind and written back after a splitter drag.
    /// </summary>
    [ObservableProperty]
    private double _prescribedWidth;

    partial void OnIsDockedRightChanged(bool value) =>
        _preferences.SplitsDock = value ? SplitsDock.Right : SplitsDock.Bottom;

    partial void OnPanelSizeChanged(double value) => _preferences.SplitsSize = value;

    partial void OnPrescribedWidthChanged(double value) => _preferences.SplitsPrescribedWidth = value;

    /// <summary>Toggles the dock side (the View's "move right / move down" button).</summary>
    [RelayCommand]
    private void ToggleDock() => IsDockedRight = !IsDockedRight;

    /// <summary>Clears the panel (called when the selection is lost or the chip is unrecognised).</summary>
    public void Clear()
    {
        Passage.Clear();
        Expected.Clear();
        Entries.Clear();
        Heading = string.Empty;
        Summary = string.Empty;
        VariantText = string.Empty;
        HasVariant = false;
        IsOrdered = false;
        IsScored = false;
        ShowPoints = false;
        HasData = false;
    }

    /// <summary>Fills the panel from a built splits view for the given selected row.</summary>
    public void Show(SplitsView view, string heading)
    {
        Passage.Clear();
        Expected.Clear();
        Entries.Clear();
        Heading = heading;

        IsOrdered = view.Layout == SplitsLayout.Ordered;
        IsScored = view.Layout == SplitsLayout.Scored;
        ShowPoints = view.HasPoints;

        HasVariant = view.VariantCode.Length > 0;
        VariantText = HasVariant
            ? $"{_localization.Get("Splits.VariantCode")} {view.VariantCode}"
            : string.Empty;

        if (IsOrdered)
        {
            foreach (var punch in view.Passage)
                Passage.Add(new PassagePunchViewModel(punch, _localization));
            foreach (var control in view.Expected)
                Expected.Add(new ExpectedControlViewModel(control, _localization));
            // Rogaine adds the points to the "visited / expected" line; set-course shows it plain.
            Summary = view.HasPoints
                ? ScoredSummary(view)
                : string.Format(_localization.Get("FinishRead.Splits.Visited"),
                    view.VisitedCount, view.ExpectedCount);
        }
        else
        {
            foreach (var entry in view.Entries)
                Entries.Add(new ScoreEntryViewModel(entry, _localization));
            Summary = ScoredSummary(view);
        }

        HasData = true;
    }

    // The "visited / expected" + points line for a scoring view. With an over-time penalty it spells out the
    // breakdown "… бали: X − Y = Z" (gross − penalty = net); otherwise just the net total.
    private string ScoredSummary(SplitsView view)
    {
        if (view.Penalty > 0)
            return string.Format(_localization.Get("FinishRead.Splits.ScoredPenalty"),
                view.VisitedCount, view.ExpectedCount, view.GrossPoints, view.Penalty, view.TotalPoints);

        return string.Format(_localization.Get("FinishRead.Splits.Scored"),
            view.VisitedCount, view.ExpectedCount, view.TotalPoints);
    }
}

/// <summary>One punch in the actual passage (ordered layout): code, on/off course, time and splits.</summary>
public sealed class PassagePunchViewModel
{
    private readonly PassagePunch _punch;
    private readonly ILocalizationService _localization;

    public PassagePunchViewModel(PassagePunch punch, ILocalizationService localization)
    {
        _punch = punch;
        _localization = localization;
    }

    /// <summary>1-based punch index in chip order. Blank for the start/finish marker rows.</summary>
    public string IndexText => _punch.Kind == PassageKind.Control ? _punch.Index.ToString() : string.Empty;

    /// <summary>Control code, or the localized "Start"/"Finish" label for the bracket rows.</summary>
    public string Code => _punch.Kind switch
    {
        PassageKind.Start => _localization.Get("FinishRead.Splits.Start"),
        PassageKind.Finish => _localization.Get("FinishRead.Splits.Finish"),
        _ => _punch.Code
    };

    /// <summary>Status glyph: ✓ on course / ✗ off course; blank for the start/finish marker rows.</summary>
    public string Glyph => _punch.Kind != PassageKind.Control
        ? string.Empty
        : _punch.OnCourse ? "✓" : "✗";

    /// <summary>Punch time as "HH:mm:ss" (in local time), or blank.</summary>
    public string TimeText => _punch.Time is { } t ? t.ToLocalTime().ToString("HH:mm:ss") : string.Empty;

    /// <summary>Leg split (time since the previous punch in chip order) as "m:ss" / "h:mm:ss", or blank.
    /// Uses the always-filled row-to-row leg (set course) so extra/off-course rows keep their time.</summary>
    public string LegText => SplitFormat.Duration(_punch.DisplayLeg ?? _punch.Leg);

    /// <summary>Elapsed since the start, or blank.</summary>
    public string ElapsedText => SplitFormat.Duration(_punch.Elapsed);

    /// <summary>Straight-line distance of this leg in metres, e.g. "420"; blank when unknown. (Unit in header.)
    /// Row-to-row distance (set course), so every row — extras included — shows a довжина.</summary>
    public string DistanceText => SplitFormat.DistanceMetres(_punch.DisplayLegKm ?? _punch.LegKm);

    /// <summary>Leg pace as "m:ss"; blank when distance or leg time is unknown. (Unit /км in header.)</summary>
    public string PaceText => SplitFormat.Pace(_punch.DisplayPace ?? _punch.PaceSecondsPerKm);

    /// <summary>Point value of this control (rogaine), e.g. "+5"; the finish row carries the over-time penalty
    /// as a negative ("−4"). Blank when it scored nothing.</summary>
    public string PointsText => _punch.Points switch
    {
        > 0 and var p => $"+{p}",
        < 0 and var p => $"−{-p}",
        _ => string.Empty
    };

    /// <summary>Running point total after this control (rogaine); blank for a non-scoring punch.</summary>
    public string RunningTotalText => _punch.RunningTotal is { } r ? r.ToString() : string.Empty;

    /// <summary>Team marker: ★ when this control counts toward the rogaine team result (every member
    /// punched it); blank otherwise. Only ever set in a team context.</summary>
    public string TeamGlyph => _punch.CountsForTeam ? "★" : string.Empty;

    /// <summary>Tooltip explaining the team marker; blank when the control doesn't count for the team.</summary>
    public string TeamTooltip => _punch.CountsForTeam ? _localization.Get("FinishRead.Splits.TeamCounts") : string.Empty;
}

/// <summary>One prescribed control (ordered layout): order, code, taken-or-missing.</summary>
public sealed class ExpectedControlViewModel
{
    private readonly ExpectedControl _control;
    private readonly ILocalizationService _localization;

    public ExpectedControlViewModel(ExpectedControl control, ILocalizationService localization)
    {
        _control = control;
        _localization = localization;
    }

    // A disabled («проблемний») control carries no course sequence (it was dropped from the required course).
    public string SequenceText => _control.Ignored ? string.Empty : _control.Sequence.ToString();
    public string Code => _control.Code;

    /// <summary>Dim a disabled or un-taken control; a taken one stays full opacity. (The view binds Taken
    /// through a bool→opacity converter, so a disabled control reads as "not taken" = dimmed.)</summary>
    public bool Taken => _control.Taken && !_control.Ignored;

    /// <summary>Status glyph: a disabled control shows ∅; otherwise ✓ taken / — missing.</summary>
    public string Glyph => _control.Ignored ? "∅" : _control.Taken ? "✓" : "—";

    /// <summary>Point value of this control (rogaine), e.g. "+5"; blank when it carries no points.</summary>
    public string PointsText => _control.Points is { } p && p != 0 ? $"+{p}" : string.Empty;

    /// <summary>"disabled" label for a problem control, "missing" for an un-taken one; blank otherwise.</summary>
    public string Note => _control.Ignored
        ? _localization.Get("FinishRead.Problematic.Disabled")
        : _control.Taken ? string.Empty : _localization.Get("FinishRead.Splits.Missing");

    /// <summary>Team marker: ★ when this control counts toward the rogaine team result (every member
    /// punched it); blank otherwise. Only ever set in a team context.</summary>
    public string TeamGlyph => _control.CountsForTeam ? "★" : string.Empty;

    /// <summary>Tooltip explaining the team marker; blank when the control doesn't count for the team.</summary>
    public string TeamTooltip => _control.CountsForTeam ? _localization.Get("FinishRead.Splits.TeamCounts") : string.Empty;
}

/// <summary>One row of the scored (score/choice/rogaine) splits panel: an allowed control + points.</summary>
public sealed class ScoreEntryViewModel
{
    private readonly ScoreEntry _entry;

    public ScoreEntryViewModel(ScoreEntry entry, ILocalizationService localization) => _entry = entry;

    public string Code => _entry.Code;
    public bool Visited => _entry.Visited;

    /// <summary>Status glyph: ✓ visited, — not visited.</summary>
    public string Glyph => _entry.Visited ? "✓" : "—";

    /// <summary>Point value, e.g. "+30"; blank when zero.</summary>
    public string PointsText => _entry.Points != 0 ? $"+{_entry.Points}" : string.Empty;

    /// <summary>Punch time as "HH:mm:ss" (in local time), or blank when not visited.</summary>
    public string TimeText => _entry.PunchTime is { } t ? t.ToLocalTime().ToString("HH:mm:ss") : string.Empty;

    /// <summary>Elapsed since the start, or blank.</summary>
    public string ElapsedText => SplitFormat.Duration(_entry.Elapsed);

    /// <summary>Running total after this control (visited rows only); blank for unvisited rows.</summary>
    public string RunningTotalText => _entry.Visited ? _entry.RunningTotal.ToString() : string.Empty;
}

/// <summary>Shared duration formatting for the splits rows.</summary>
internal static class SplitFormat
{
    /// <summary>"m:ss" under an hour, "h:mm:ss" at or above; blank for null/negative.</summary>
    public static string Duration(TimeSpan? span) => span is { } s && s >= TimeSpan.Zero
        ? (s.TotalHours >= 1 ? s.ToString("h\\:mm\\:ss") : s.ToString("m\\:ss"))
        : string.Empty;

    /// <summary>Leg distance in whole metres, e.g. "420" (unit lives in the header); blank for null/zero.</summary>
    public static string DistanceMetres(decimal? km) => km is { } d && d > 0m
        ? Math.Round(d * 1000m).ToString("0")
        : string.Empty;

    /// <summary>Pace as "m:ss" from seconds-per-km (unit /км lives in the header); blank for null/non-positive.</summary>
    public static string Pace(double? secondsPerKm)
    {
        if (secondsPerKm is not { } s || s <= 0 || double.IsInfinity(s))
            return string.Empty;
        var span = TimeSpan.FromSeconds(Math.Round(s));
        return $"{(int)span.TotalMinutes}:{span.Seconds:00}";
    }
}
