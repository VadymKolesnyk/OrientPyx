using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Localization;

namespace OrientPyx.Presentation.ViewModels.Pages;

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
    private readonly int _participantCount;
    private readonly IDisciplineStrategyProvider _strategies;
    private readonly Action<GroupDayRowViewModel> _requestSave;

    // Suppresses save requests while the constructor seeds initial values.
    private readonly bool _initialized;

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CourseOrderDisplay))]
    private string _courseOrder;

    // How many scatter («розсіювання») variants this group has (0 = not a scatter course). Kept live by the
    // page's bottom variants editor so the grid cell's «N варіантів дистанції» updates as variants are edited.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CourseOrderDisplay))]
    [NotifyPropertyChangedFor(nameof(IsScatter))]
    [NotifyPropertyChangedFor(nameof(CanEditCourseOrderInline))]
    private int _scatterVariantCount;

    // Control-point count of the LONGEST scatter variant. For a scatter group the grid's control-count cell
    // reports this (the single-order field is unused), so it's kept live by the page's variants editor too.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ControlCountText))]
    private int _scatterMaxControlCount;

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

    [ObservableProperty]
    private string _courseSetter;

    [ObservableProperty]
    private string _courseSetterCategory;

    [ObservableProperty]
    private PointsRuleOption _selectedPointsRule;

    [ObservableProperty]
    private RankLevelOption _selectedRankLevel;

    [ObservableProperty]
    private string _masterCountText;

    /// <summary>Earliest allowed birth year, inclusive ("не старше"); blank = no lower bound. Group-level.</summary>
    [ObservableProperty]
    private string _minBirthYearText;

    /// <summary>Latest allowed birth year, inclusive ("не молодше"); blank = no upper bound. Group-level.</summary>
    [ObservableProperty]
    private string _maxBirthYearText;

    /// <summary>
    /// The competition-wide course-setter, shown as the cell placeholder when this group's own override
    /// is blank (so an empty cell reads the inherited global value, greyed). Kept live by the page when
    /// the global value changes above the table.
    /// </summary>
    [ObservableProperty]
    private string _courseSetterPlaceholder = string.Empty;

    /// <summary>
    /// The competition-wide course-setter judge category (the raw global value). The cell binds to
    /// <see cref="EffectiveCourseSetterCategoryPlaceholder"/>, not this, so the global category is only
    /// suggested when the group hasn't overridden the course-setter name.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveCourseSetterCategoryPlaceholder))]
    private string _courseSetterCategoryPlaceholder = string.Empty;

    /// <summary>
    /// The category placeholder actually shown in the cell: the global category, but only while this
    /// group inherits the course-setter name (its own <see cref="CourseSetter"/> is blank). Once the
    /// group sets its own course-setter, it no longer inherits the global category as a hint.
    /// </summary>
    public string EffectiveCourseSetterCategoryPlaceholder =>
        string.IsNullOrWhiteSpace(CourseSetter) ? CourseSetterCategoryPlaceholder : string.Empty;

    public GroupDayRowViewModel(
        GroupDayRow row,
        ILocalizationService localization,
        IDisciplineStrategyProvider strategies,
        IReadOnlyList<PointsRuleOption> pointsRuleOptions,
        string courseSetterPlaceholder,
        string courseSetterCategoryPlaceholder,
        Action<GroupDayRowViewModel> requestSave)
    {
        _settingsId = row.SettingsId;
        _groupId = row.GroupId;
        _order = row.Order;
        _dayDefaultDiscipline = row.DayDefaultDiscipline;
        _participantCount = row.ParticipantCount;
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
        _scatterVariantCount = row.ScatterVariantCount;
        _scatterMaxControlCount = row.ScatterMaxControlCount;
        _distanceText = FormatDecimal(row.DistanceKm);
        _requiredCountText = FormatInt(row.RequiredControlCount);
        _penaltyText = FormatDecimal(row.PenaltyPerMinute);
        _timeLimitText = FormatTime(row.TimeLimitSeconds);
        _courseSetter = row.CourseSetter;
        _courseSetterCategory = row.CourseSetterCategory;
        _courseSetterPlaceholder = courseSetterPlaceholder ?? string.Empty;
        _courseSetterCategoryPlaceholder = courseSetterCategoryPlaceholder ?? string.Empty;
        _selectedDiscipline = DisciplineOptions.First(o => o.Value == row.DisciplineOverride);

        // Points-rule options are the shared list [default sentinel + every rule]. Match the stored id;
        // if it no longer exists (rule deleted), prepend a one-off "unknown" option just for this row so
        // the choice still shows rather than silently snapping to default.
        var match = pointsRuleOptions.FirstOrDefault(o => o.Id == row.PointsRuleId);
        if (match is null && row.PointsRuleId is { } missingId)
        {
            PointsRuleOptions = [PointsRuleOption.Unknown(missingId, localization), .. pointsRuleOptions];
            match = PointsRuleOptions[0];
        }
        else
        {
            PointsRuleOptions = pointsRuleOptions;
            match ??= pointsRuleOptions.First(o => o.Id is null);
        }
        _selectedPointsRule = match;

        // Rank-level options: one per GroupRankLevel value (None / Adult / Junior).
        RankLevelOptions =
        [
            new RankLevelOption(GroupRankLevel.None, localization),
            new RankLevelOption(GroupRankLevel.Adult, localization),
            new RankLevelOption(GroupRankLevel.Junior, localization),
        ];
        _selectedRankLevel = RankLevelOptions.First(o => o.Value == row.RankLevel);
        _masterCountText = FormatInt(row.MasterCount);
        _minBirthYearText = FormatInt(row.MinBirthYear);
        _maxBirthYearText = FormatInt(row.MaxBirthYear);

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

    /// <summary>Points-rule options (default sentinel + every rule) shown in the Points ComboBox.</summary>
    public IReadOnlyList<PointsRuleOption> PointsRuleOptions { get; }

    /// <summary>Rank-level options (None / Adult / Junior) shown in the Rank-level ComboBox.</summary>
    public IReadOnlyList<RankLevelOption> RankLevelOptions { get; }

    /// <summary>Effective discipline = the per-group override, or the day default when none is set.</summary>
    public DisciplineType EffectiveDiscipline => SelectedDiscipline.Value ?? _dayDefaultDiscipline;

    private IDisciplineStrategy Strategy => _strategies.For(EffectiveDiscipline);

    /// <summary>True when this is a scatter («розсіювання») group — its discipline is Scatter.</summary>
    public bool IsScatter => EffectiveDiscipline == DisciplineType.Scatter;

    /// <summary>
    /// What the grid's course-order cell shows: for a scatter group, «N варіантів дистанції» (there is no
    /// single order — variants are edited in the bottom panel); otherwise the plain course-order string.
    /// </summary>
    public string CourseOrderDisplay => IsScatter
        ? string.Format(Localization.Get("Groups.Col.CourseOrder.Variants"), ScatterVariantCount)
        : CourseOrder;

    /// <summary>
    /// Whether the course-order cell is editable inline in the grid. A scatter group edits its several orders
    /// in the bottom variants table instead, so its cell is read-only (and shows the variant count).
    /// </summary>
    public bool CanEditCourseOrderInline => UsesCourseOrder && !IsScatter;

    /// <summary>
    /// Read-only count of control points, auto-computed from the course/control list for every
    /// discipline. A scatter («розсіювання») group has no single order — it reports the control count of
    /// its longest variant instead (the single-order field is unused for scatter).
    /// </summary>
    public string ControlCountText =>
        (IsScatter ? ScatterMaxControlCount : Strategy.ControlCount(CourseOrder))
            .ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Read-only count of participants in this group on this day, computed by the editor service from
    /// the day's participant links and refreshed on each page load.
    /// </summary>
    public string ParticipantCountText =>
        _participantCount.ToString(CultureInfo.InvariantCulture);

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
        PenaltyPerMinute: ParseDecimal(PenaltyText),
        CourseSetter: (CourseSetter ?? string.Empty).Trim(),
        CourseSetterCategory: (CourseSetterCategory ?? string.Empty).Trim(),
        PointsRuleId: SelectedPointsRule.Id,
        RankLevel: SelectedRankLevel.Value,
        MasterCount: ParseInt(MasterCountText),
        MinBirthYear: ParseInt(MinBirthYearText),
        MaxBirthYear: ParseInt(MaxBirthYearText));

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
    partial void OnCourseSetterChanged(string value)
    {
        // The category hint only applies while the group inherits the course-setter name (see the
        // effective placeholder), so re-evaluate it whenever the name changes.
        OnPropertyChanged(nameof(EffectiveCourseSetterCategoryPlaceholder));
        QueueSave();
    }

    partial void OnCourseSetterCategoryChanged(string value) => QueueSave();
    partial void OnSelectedPointsRuleChanged(PointsRuleOption value) => QueueSave();
    partial void OnSelectedRankLevelChanged(RankLevelOption value) => QueueSave();
    partial void OnMasterCountTextChanged(string value) => QueueSave();
    partial void OnMinBirthYearTextChanged(string value) => QueueSave();
    partial void OnMaxBirthYearTextChanged(string value) => QueueSave();

    partial void OnSelectedDisciplineChanged(DisciplineOverrideOption value)
    {
        // The effective discipline changed, so re-evaluate every derived column state.
        OnPropertyChanged(nameof(EffectiveDiscipline));
        OnPropertyChanged(nameof(ControlCountText));
        OnPropertyChanged(nameof(UsesCourseOrder));
        OnPropertyChanged(nameof(UsesRequiredCount));
        OnPropertyChanged(nameof(UsesPenalty));
        OnPropertyChanged(nameof(UsesTimeLimit));
        OnPropertyChanged(nameof(IsScatter));
        OnPropertyChanged(nameof(CourseOrderDisplay));
        OnPropertyChanged(nameof(CanEditCourseOrderInline));

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
