using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using OrientDesk.BusinessLogic.Disciplines;
using OrientDesk.BusinessLogic.Enums;
using OrientDesk.BusinessLogic.Models;
using OrientDesk.Localization;

namespace OrientDesk.Presentation.ViewModels.Pages;

/// <summary>
/// One editable row in the groups grid. Wraps a single <see cref="GroupDayRow"/> (a group joined
/// with its settings on the current day). Edits do not save directly — each change invokes the
/// page-supplied <c>requestSave</c> callback, which debounces and persists in the background.
///
/// Which type-specific cells are relevant (course order, control count, required count, penalty,
/// time limit) is decided by the row's effective discipline via <see cref="IDisciplineStrategy"/> —
/// no competition rules live here.
/// </summary>
public sealed partial class GroupDayRowViewModel : ObservableObject
{
    private readonly Guid _settingsId;
    private readonly Guid _groupId;
    private readonly int _order;
    private readonly DisciplineType _dayDefaultDiscipline;
    private readonly IDisciplineStrategyProvider _strategies;
    private readonly Action<GroupDayRowViewModel> _requestSave;

    // Suppresses save requests while the constructor seeds initial values.
    private readonly bool _initialized;

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string _courseOrder;

    [ObservableProperty]
    private string _distanceText;

    [ObservableProperty]
    private string _requiredCountText;

    [ObservableProperty]
    private string _penaltyText;

    [ObservableProperty]
    private string _timeLimitText;

    [ObservableProperty]
    private DisciplineOverrideOption _selectedDiscipline;

    public GroupDayRowViewModel(
        GroupDayRow row,
        ILocalizationService localization,
        IDisciplineStrategyProvider strategies,
        Action<GroupDayRowViewModel> requestSave)
    {
        _settingsId = row.SettingsId;
        _groupId = row.GroupId;
        _order = row.Order;
        _dayDefaultDiscipline = row.DayDefaultDiscipline;
        _strategies = strategies;
        _requestSave = requestSave;
        Localization = localization;

        // "(default: <day default>)" sentinel first, then one option per registered discipline.
        DisciplineOptions =
        [
            new DisciplineOverrideOption(null, localization, row.DayDefaultDiscipline),
            .. strategies.All.Select(s => new DisciplineOverrideOption(s.Type, localization))
        ];

        _name = row.Name;
        _courseOrder = row.CourseOrder;
        _distanceText = FormatDecimal(row.DistanceKm);
        _requiredCountText = FormatInt(row.RequiredControlCount);
        _penaltyText = FormatDecimal(row.PenaltyPerMinute);
        _timeLimitText = FormatTime(row.TimeLimitSeconds);
        _selectedDiscipline = DisciplineOptions.First(o => o.Value == row.DisciplineOverride);

        // Rogaine penalises over-time by a default rate (1 бал/min), so show that default in the cell when the
        // group set none — the user can still change or clear it (clearing falls back to the same default).
        if (_penaltyText.Length == 0 && Strategy.DefaultPenaltyPerMinute is { } rate)
            _penaltyText = FormatDecimal(rate);

        _initialized = true;
    }

    public ILocalizationService Localization { get; }

    /// <summary>Key used by the page for debounce timers, delete and as the row identity.</summary>
    public Guid Id => _settingsId;

    /// <summary>Parent group id; needed for the cascade-delete check on removal.</summary>
    public Guid GroupId => _groupId;

    /// <summary>Discipline options (value + localized label) shown in the Discipline ComboBox.</summary>
    public IReadOnlyList<DisciplineOverrideOption> DisciplineOptions { get; }

    /// <summary>Effective discipline = the per-group override, or the day default when none is set.</summary>
    public DisciplineType EffectiveDiscipline => SelectedDiscipline.Value ?? _dayDefaultDiscipline;

    private IDisciplineStrategy Strategy => _strategies.For(EffectiveDiscipline);

    /// <summary>
    /// Read-only count of control points, auto-computed from the course/control list for every
    /// discipline.
    /// </summary>
    public string ControlCountText =>
        Strategy.ControlCount(CourseOrder).ToString(CultureInfo.InvariantCulture);

    // Per-column relevance for the current effective discipline — drives cell enable/dim in the grid.
    public bool UsesCourseOrder => Strategy.UsesColumn(GroupColumn.CourseOrder);
    public bool UsesRequiredCount => Strategy.UsesColumn(GroupColumn.RequiredControlCount);
    public bool UsesPenalty => Strategy.UsesColumn(GroupColumn.PenaltyPerMinute);
    public bool UsesTimeLimit => Strategy.UsesColumn(GroupColumn.TimeLimit);

    public GroupDayRow ToRow() => new(
        SettingsId: _settingsId,
        GroupId: _groupId,
        Order: _order,
        Name: (Name ?? string.Empty).Trim(),
        CourseOrder: (CourseOrder ?? string.Empty).Trim(),
        DistanceKm: ParseDecimal(DistanceText),
        DisciplineOverride: SelectedDiscipline.Value,
        DayDefaultDiscipline: _dayDefaultDiscipline,
        TimeLimitSeconds: ParseTime(TimeLimitText),
        RequiredControlCount: ParseInt(RequiredCountText),
        PenaltyPerMinute: ParseDecimal(PenaltyText));

    partial void OnNameChanged(string value) => QueueSave();

    partial void OnCourseOrderChanged(string value)
    {
        OnPropertyChanged(nameof(ControlCountText));
        QueueSave();
    }

    partial void OnDistanceTextChanged(string value) => QueueSave();
    partial void OnRequiredCountTextChanged(string value) => QueueSave();
    partial void OnPenaltyTextChanged(string value) => QueueSave();
    partial void OnTimeLimitTextChanged(string value) => QueueSave();

    partial void OnSelectedDisciplineChanged(DisciplineOverrideOption value)
    {
        // The effective discipline changed, so re-evaluate every derived column state.
        OnPropertyChanged(nameof(EffectiveDiscipline));
        OnPropertyChanged(nameof(ControlCountText));
        OnPropertyChanged(nameof(UsesCourseOrder));
        OnPropertyChanged(nameof(UsesRequiredCount));
        OnPropertyChanged(nameof(UsesPenalty));
        OnPropertyChanged(nameof(UsesTimeLimit));

        // Switching to a discipline with a default penalty (rogaine) shows that default when the cell is
        // empty, mirroring the constructor — so a freshly-switched rogaine group reads "1" rather than blank.
        if (PenaltyText.Length == 0 && Strategy.DefaultPenaltyPerMinute is { } rate)
            PenaltyText = FormatDecimal(rate);

        QueueSave();
    }

    private void QueueSave()
    {
        if (_initialized)
            _requestSave(this);
    }

    // Time limit (контрольний час) is stored as a count of seconds but edited as hh:mm:ss.
    private static string FormatTime(int? totalSeconds)
    {
        if (totalSeconds is null)
            return string.Empty;

        var t = TimeSpan.FromSeconds(totalSeconds.Value);
        return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
    }

    // Accepts "ss", "mm:ss" or "hh:mm:ss" (each part optional/partial), returning total seconds.
    private static int? ParseTime(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var parts = text.Trim().Split(':');
        if (parts.Length > 3)
            return null;

        var seconds = 0;
        foreach (var part in parts)
        {
            // Treat blank groups (e.g. trailing "1:" while typing) as zero.
            var value = part.Length == 0
                ? 0
                : int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : -1;
            if (value < 0)
                return null;

            seconds = seconds * 60 + value;
        }

        return seconds;
    }

    private static string FormatInt(int? value)
        => value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    private static int? ParseInt(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        return int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static string FormatDecimal(decimal? value)
        => value?.ToString("0.######", CultureInfo.InvariantCulture) ?? string.Empty;

    private static decimal? ParseDecimal(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var normalized = text.Trim().Replace(',', '.');
        return decimal.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }
}
