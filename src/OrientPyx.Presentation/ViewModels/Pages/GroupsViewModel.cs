using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.BusinessLogic.Disciplines.CoursePattern;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Localization;
using OrientPyx.Presentation.Services;
using OrientPyx.Presentation.ViewModels.Dialogs;

namespace OrientPyx.Presentation.ViewModels.Pages;

/// <summary>
/// Spreadsheet-like groups table for the CURRENT competition day. A group exists at the
/// competition level; its presence here is a per-day attachment. Adding by name reuses an existing
/// group; deleting removes it only from this day (and the group entirely once it runs on no day).
/// Cells auto-save in the background (debounced per row) — no Save button, no busy overlay.
/// </summary>
public sealed partial class GroupsViewModel : PageViewModelBase
{
    private static readonly TimeSpan SaveDebounce = TimeSpan.FromMilliseconds(600);

    private readonly ICompetitionEditorService _editor;
    private readonly ISessionService _session;
    private readonly IXmlImportFlow _importFlow;
    private readonly IBusyService _busy;
    private readonly IDisciplineStrategyProvider _strategies;
    private readonly IDialogService _dialogs;
    private readonly IAppStore _appStore;
    private readonly Dictionary<Guid, CancellationTokenSource> _saveTimers = new();

    // Used to debounce the competition-level settings strip (course-setter + default points rule).
    private CancellationTokenSource? _infoSaveCts;

    // The app-level points rules, loaded once per LoadAsync (shared across every row's combo).
    private IReadOnlyList<PointsRule> _pointsRules = [];

    // The current competition metadata being edited by the top settings strip.
    private CompetitionInfo? _info;

    public GroupsViewModel(
        ILocalizationService localization,
        ICompetitionEditorService editor,
        ISessionService session,
        IXmlImportFlow importFlow,
        IBusyService busy,
        IDisciplineStrategyProvider strategies,
        IDialogService dialogs,
        IAppStore appStore,
        ITableLayoutStore layoutStore)
        : base(localization)
    {
        LayoutStore = layoutStore;
        _editor = editor;
        _session = session;
        _importFlow = importFlow;
        _busy = busy;
        _strategies = strategies;
        _dialogs = dialogs;
        _appStore = appStore;
        Localization.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(SelectedCourseHeader));
            RefreshPatternPreview();
            if (SelectedGroup is { } sg && sg.EffectiveDiscipline == DisciplineType.Scatter)
                ScatterHeader = string.Format(Localization.Get("Groups.Scatter.Header"), sg.ScatterVariantCount);
        };
        // Singleton VM: when the competition/day changes, drop the previous event's rows so the
        // page never shows stale data before it is next opened. The event can be raised on a pool
        // thread (session writes run inside RunAsync), so marshal LoadAsync onto the UI thread.
        _session.SessionChanged += (_, _) => Dispatcher.UIThread.Post(() => _ = LoadAsync());
    }

    /// <summary>Per-competition table-view store; persists this page's table column order/width/visibility.</summary>
    public ITableLayoutStore LayoutStore { get; }

    public override string NavKey => "Nav.Groups";
    public override string TitleKey => "Page.Groups.Title";
    public override string TextKey => "Page.Groups.Text";

    public ObservableCollection<GroupDayRowViewModel> Groups { get; } = [];

    /// <summary>Selectable days for the top-right day picker.</summary>
    public ObservableCollection<DayOption> DayOptions { get; } = [];

    [ObservableProperty]
    private DayOption? _selectedDay;

    /// <summary>Day picker is shown only when the competition has more than one day.</summary>
    public bool ShowDaySelector => DayOptions.Count > 1;

    // ── Competition-level settings (top "Загальні налаштування" strip) ──────────────────────────────
    // These are competition-wide defaults edited above the table: the global course-setter (начальник
    // дистанції, which a group may override per day) and the default points rule (overridable per group).
    // Persisted to CompetitionInfo, debounced. _suppressInfoSave guards the seeding in LoadAsync.

    private bool _suppressInfoSave;

    /// <summary>Whether the top "Загальні налаштування" card is expanded (collapsed by default).</summary>
    [ObservableProperty]
    private bool _isGlobalSettingsExpanded;

    [RelayCommand]
    private void ToggleGlobalSettings() => IsGlobalSettingsExpanded = !IsGlobalSettingsExpanded;

    /// <summary>Opens the read-only help explaining how to write the «mixed» discipline's order pattern.</summary>
    [RelayCommand]
    private Task ShowCoursePatternHelpAsync() =>
        _dialogs.ShowCoursePatternHelpAsync(new CoursePatternHelpViewModel(Localization));

    /// <summary>Competition-wide course-setter name (начальник дистанції). A group may override it per day.</summary>
    [ObservableProperty]
    private string _defaultCourseSetter = string.Empty;

    /// <summary>Optional judge category for the competition-wide course-setter.</summary>
    [ObservableProperty]
    private string _defaultCourseSetterCategory = string.Empty;

    /// <summary>Options for the competition default points rule: a "(none)" sentinel + every rule.</summary>
    public ObservableCollection<PointsRuleOption> DefaultPointsRuleOptions { get; } = [];

    /// <summary>The selected competition-wide default points rule (null id option = no default).</summary>
    [ObservableProperty]
    private PointsRuleOption? _selectedDefaultPointsRule;

    // The row whose course / control list is shown in the bottom editor. Null when nothing is
    // selected, which dims the editor and shows a hint instead.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedCourseHeader))]
    [NotifyPropertyChangedFor(nameof(HasSelectedGroup))]
    private GroupDayRowViewModel? _selectedGroup;

    /// <summary>True when a group is selected, so the bottom course editor is enabled.</summary>
    public bool HasSelectedGroup => SelectedGroup is not null;

    /// <summary>Caption above the bottom course editor — names the selected group, or prompts.</summary>
    public string SelectedCourseHeader => SelectedGroup is { } row
        ? string.Format(
            Localization.Get("Groups.SelectedCourse"),
            string.IsNullOrWhiteSpace(row.Name) ? "—" : row.Name)
        : Localization.Get("Groups.SelectedCourse.None");

    // ── «Mixed» course-order pattern live preview ────────────────────────────────────────────────────
    // Only meaningful when the selected group's effective discipline is Mixed: the order field is then a
    // pattern, so we parse it on every keystroke and show either the normalized "S … F" order (valid) or
    // the first structural error (invalid). The day's start/finish codes label the S/F markers.

    private string? _startCode;
    private string? _finishCode;

    /// <summary>True when the selected group runs the «mixed» discipline, so the pattern preview shows.</summary>
    [ObservableProperty]
    private bool _isPatternPreviewVisible;

    /// <summary>True when the current pattern parsed without a structural error (drives the ✔/✖ glyph).</summary>
    [ObservableProperty]
    private bool _isPatternValid = true;

    /// <summary>The normalized order ("S … F") when valid, or the localized error message when invalid.</summary>
    [ObservableProperty]
    private string _patternPreviewText = string.Empty;

    // ── Scatter («розсіювання») variants editor (bottom panel) ───────────────────────────────────────
    // Shown only when the selected group's effective discipline is Scatter: the group has several valid
    // orders (variants), edited here as a Код / Дистанція table. Each edit debounces a background save that
    // replaces the group's variant set; the grid's «N варіантів дистанції» cell is kept live off the count.

    private CancellationTokenSource? _scatterSaveCts;
    // Guards the save trigger while LoadScatterVariants seeds the rows.
    private bool _suppressScatterSave;
    // The group whose variants are currently loaded into the editor (so a save targets the right group even
    // after the selection changed).
    private Guid? _scatterGroupId;

    /// <summary>True when the selected group runs the scatter discipline, so the variants editor shows.</summary>
    [ObservableProperty]
    private bool _isScatterEditorVisible;

    /// <summary>Caption above the scatter variants editor, e.g. «Варіанти розсіювання — 2».</summary>
    [ObservableProperty]
    private string _scatterHeader = string.Empty;

    /// <summary>The editable scatter variant rows for the selected group (Код + Дистанція).</summary>
    public ObservableCollection<ScatterVariantRowViewModel> ScatterVariants { get; } = [];

    /// <summary>True when the selected scatter group has no variants yet (shows the empty hint).</summary>
    public bool HasNoScatterVariants => ScatterVariants.Count == 0;

    // Rebuilds the scatter editor from the selected group. Hidden (and cleared) unless the selected group is
    // a scatter-discipline group; otherwise loads its variants off the DB into editable rows.
    private async Task RefreshScatterEditorAsync()
    {
        // Detach the old rows' change handlers before clearing.
        foreach (var v in ScatterVariants)
            v.Changed -= OnScatterRowChanged;
        ScatterVariants.Clear();

        var row = SelectedGroup;
        if (row is null || row.EffectiveDiscipline != DisciplineType.Scatter)
        {
            _scatterGroupId = null;
            IsScatterEditorVisible = false;
            OnPropertyChanged(nameof(HasNoScatterVariants));
            return;
        }

        IsScatterEditorVisible = true;
        _scatterGroupId = row.GroupId;
        ScatterHeader = string.Format(Localization.Get("Groups.Scatter.Header"), row.ScatterVariantCount);

        var variants = await _editor.GetScatterVariantsAsync(row.GroupId);

        _suppressScatterSave = true;
        try
        {
            foreach (var v in variants)
            {
                var vm = new ScatterVariantRowViewModel(v.Code, v.CourseOrder);
                vm.Changed += OnScatterRowChanged;
                ScatterVariants.Add(vm);
            }
        }
        finally
        {
            _suppressScatterSave = false;
        }

        OnPropertyChanged(nameof(HasNoScatterVariants));
    }

    [RelayCommand]
    private void AddScatterVariant()
    {
        var vm = new ScatterVariantRowViewModel(string.Empty, string.Empty);
        vm.Changed += OnScatterRowChanged;
        ScatterVariants.Add(vm);
        OnPropertyChanged(nameof(HasNoScatterVariants));
        QueueScatterSave();
    }

    [RelayCommand]
    private void RemoveScatterVariant(ScatterVariantRowViewModel? row)
    {
        if (row is null)
            return;
        row.Changed -= OnScatterRowChanged;
        ScatterVariants.Remove(row);
        OnPropertyChanged(nameof(HasNoScatterVariants));
        QueueScatterSave();
    }

    private void OnScatterRowChanged() => QueueScatterSave();

    private void QueueScatterSave()
    {
        if (_suppressScatterSave || _scatterGroupId is null)
            return;

        _scatterSaveCts?.Cancel();
        var cts = new CancellationTokenSource();
        _scatterSaveCts = cts;
        _ = SaveScatterDebouncedAsync(_scatterGroupId.Value, cts.Token);
    }

    private async Task SaveScatterDebouncedAsync(Guid groupId, CancellationToken token)
    {
        try
        {
            await Task.Delay(SaveDebounce, token);

            // Snapshot the editor rows (UI thread) before the SQLite write.
            var rows = ScatterVariants
                .Select(v => new ScatterVariantRow(v.Code, v.CourseOrder))
                .ToList();

            await Task.Run(() => _editor.SaveScatterVariantsAsync(groupId, rows, token), token);

            // Keep the grid's «N варіантів дистанції» cell in sync with the saved (non-blank) count.
            var count = rows.Count(r =>
                !string.IsNullOrWhiteSpace(r.Code) || !string.IsNullOrWhiteSpace(r.CourseOrder));
            var target = Groups.FirstOrDefault(g => g.GroupId == groupId);
            if (target is not null)
            {
                target.ScatterVariantCount = count;
                if (ReferenceEquals(target, SelectedGroup))
                    ScatterHeader = string.Format(Localization.Get("Groups.Scatter.Header"), count);
            }
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

    // Recomputes the pattern preview from the selected group's course order. No-op (hidden) unless the
    // selected group is a mixed-discipline group.
    private void RefreshPatternPreview()
    {
        var row = SelectedGroup;
        if (row is null || row.EffectiveDiscipline != DisciplineType.Mixed)
        {
            IsPatternPreviewVisible = false;
            IsPatternValid = true;
            PatternPreviewText = string.Empty;
            return;
        }

        IsPatternPreviewVisible = true;
        var pattern = CoursePattern.Parse(row.CourseOrder, _startCode, _finishCode);
        IsPatternValid = pattern.IsValid;
        PatternPreviewText = pattern.IsValid
            ? pattern.NormalizedOrder()
            : DescribeError(pattern.Errors[0]);
    }

    // Maps a parse error to a localized, human-readable message (with the offending token spliced in).
    private string DescribeError(CoursePatternError error)
    {
        var key = error.Kind switch
        {
            CoursePatternErrorKind.UnbalancedBracket => "CoursePattern.Error.UnbalancedBracket",
            CoursePatternErrorKind.ChoiceMissingColon => "CoursePattern.Error.ChoiceMissingColon",
            CoursePatternErrorKind.ChoiceBadCount => "CoursePattern.Error.ChoiceBadCount",
            CoursePatternErrorKind.EmptyChoiceBlock => "CoursePattern.Error.EmptyChoiceBlock",
            CoursePatternErrorKind.ChoiceCountTooLarge => "CoursePattern.Error.ChoiceCountTooLarge",
            CoursePatternErrorKind.EmptyOrderedBlock => "CoursePattern.Error.EmptyOrderedBlock",
            _ => "CoursePattern.Error.Generic"
        };
        return string.Format(Localization.Get(key), error.Token);
    }

    // Type-specific columns are shown only when at least one row's effective discipline uses them.
    // (Course order, control count and time limit are used by every discipline, so they have no toggle.)
    public bool ShowRequiredCountColumn => Groups.Any(r => r.UsesRequiredCount);
    public bool ShowPenaltyColumn => Groups.Any(r => r.UsesPenalty);

    /// <summary>Raised when the set of visible type-specific columns may have changed; the view
    /// re-applies column visibility in response (columns live outside the visual tree).</summary>
    public event EventHandler? ColumnsChanged;

    // True while LoadAsync syncs SelectedDay to the session, so the setter does NOT call
    // SetCurrentDayAsync (which would re-raise SessionChanged → LoadAsync in a loop).
    private bool _syncingDay;

    /// <summary>Reloads the groups for the current day. Called when the page is shown.</summary>
    public async Task LoadAsync()
    {
        CancelAllTimers();

        // Both BD reads run off the UI thread; every collection/property write below happens
        // afterwards on the UI thread (SQLite has no real async I/O, so this can't stay inline).
        var hasDay = _session.CurrentDay is not null;
        var (days, rows, rules, info, controlPoints) = await _busy.RunAsync(async () =>
        {
            var d = await _editor.GetDaysAsync();
            var r = hasDay ? await _editor.GetGroupDayRowsAsync() : (IReadOnlyList<GroupDayRow>)[];
            var p = await _appStore.GetPointsRulesAsync();
            var i = await _editor.GetInfoAsync();
            var cp = hasDay ? await _editor.GetControlPointsAsync() : (IReadOnlyList<ControlPoint>)[];
            return (d, r, p, i, cp);
        });

        // The day's start/finish control codes — used only to label the «mixed» pattern preview's
        // S … F markers with the actual boxes (falls back to "S"/"F" when a day has none set).
        _startCode = controlPoints.FirstOrDefault(c => c.Type == ControlPointType.Start)?.Code.Trim();
        _finishCode = controlPoints.FirstOrDefault(c => c.Type == ControlPointType.Finish)?.Code.Trim();

        _pointsRules = rules;
        _info = info;

        foreach (var existing in Groups)
            existing.PropertyChanged -= OnRowPropertyChanged;
        Groups.Clear();
        SelectedGroup = null;

        _syncingDay = true;
        try
        {
            // Rebuild the options only when the day set actually changed; otherwise keep the
            // existing DayOption instances so the ComboBox's SelectedItem stays a valid reference
            // (a fresh list would leave the picker showing nothing after a day switch).
            if (!SameDays(days))
            {
                DayOptions.Clear();
                foreach (var day in days)
                    DayOptions.Add(new DayOption(day, Localization));
            }

            var current = _session.CurrentDay?.Number;
            SelectedDay = DayOptions.FirstOrDefault(o => o.Number == current) ?? DayOptions.FirstOrDefault();
        }
        finally
        {
            _syncingDay = false;
        }

        OnPropertyChanged(nameof(ShowDaySelector));

        SeedCompetitionSettings();

        // One shared per-group options list for this load (default sentinel + every rule).
        _rowPointsRuleOptions = BuildRowPointsRuleOptions();

        foreach (var row in rows)
            Groups.Add(CreateRow(row));

        RaiseColumnVisibility();
    }

    // The shared per-group points-rule options for the current load (rebuilt each LoadAsync).
    private IReadOnlyList<PointsRuleOption> _rowPointsRuleOptions = [];

    // Name of a points rule by id, or null when unset/unknown — used to label the per-group "(default: …)"
    // sentinel and the competition default combo.
    private string? RuleName(Guid? id) =>
        id is { } gid ? _pointsRules.FirstOrDefault(r => r.Id == gid)?.Name : null;

    // Fills the top settings strip from the loaded CompetitionInfo, and (re)builds both the competition
    // default-rule options and the seed for each row's per-group options. Guarded so the property setters
    // don't trigger a save while we seed.
    private void SeedCompetitionSettings()
    {
        _suppressInfoSave = true;
        try
        {
            DefaultCourseSetter = _info?.CourseSetter ?? string.Empty;
            DefaultCourseSetterCategory = _info?.CourseSetterCategory ?? string.Empty;

            // Competition default combo: a plain "немає" option (no default rule, stored as null) + every rule.
            DefaultPointsRuleOptions.Clear();
            DefaultPointsRuleOptions.Add(PointsRuleOption.None(Localization, explicitChoice: false));
            foreach (var rule in _pointsRules)
                DefaultPointsRuleOptions.Add(PointsRuleOption.ForRule(rule.Id, rule.Name, Localization));

            var defId = _info?.DefaultPointsRuleId;
            var match = DefaultPointsRuleOptions.FirstOrDefault(o => o.Id == defId);
            if (match is null && defId is { } missing)
            {
                DefaultPointsRuleOptions.Insert(0, PointsRuleOption.Unknown(missing, Localization));
                match = DefaultPointsRuleOptions[0];
            }
            SelectedDefaultPointsRule = match ?? DefaultPointsRuleOptions.FirstOrDefault(o => o.Id is null);
        }
        finally
        {
            _suppressInfoSave = false;
        }
    }

    // Builds the shared per-group points-rule options: a "(default: <competition default name>)" sentinel,
    // an explicit "немає" (no rule, ignore the default), then every rule. Rebuilt per LoadAsync so it
    // reflects the current rule set and default name.
    private IReadOnlyList<PointsRuleOption> BuildRowPointsRuleOptions()
    {
        var options = new List<PointsRuleOption>
        {
            PointsRuleOption.Default(Localization, RuleName(_info?.DefaultPointsRuleId)),
            PointsRuleOption.None(Localization, explicitChoice: true)
        };
        options.AddRange(_pointsRules.Select(r => PointsRuleOption.ForRule(r.Id, r.Name, Localization)));
        return options;
    }

    // Builds a row VM wired with the discipline provider and a watch on its discipline so the grid's
    // column visibility refreshes when a group's effective type changes.
    private GroupDayRowViewModel CreateRow(GroupDayRow row)
    {
        var vm = new GroupDayRowViewModel(
            row, Localization, _strategies, _rowPointsRuleOptions,
            (DefaultCourseSetter ?? string.Empty).Trim(),
            (DefaultCourseSetterCategory ?? string.Empty).Trim(),
            RequestRowSave);
        vm.PropertyChanged += OnRowPropertyChanged;
        return vm;
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GroupDayRowViewModel.EffectiveDiscipline))
            RaiseColumnVisibility();

        // Keep the bottom editor's caption in sync while the selected group is being renamed in-grid.
        if (e.PropertyName == nameof(GroupDayRowViewModel.Name) && ReferenceEquals(sender, SelectedGroup))
            OnPropertyChanged(nameof(SelectedCourseHeader));

        // Live-update the «mixed» pattern preview as the selected group's order is typed or its
        // discipline is switched.
        if (ReferenceEquals(sender, SelectedGroup)
            && e.PropertyName is nameof(GroupDayRowViewModel.CourseOrder)
                or nameof(GroupDayRowViewModel.EffectiveDiscipline))
            RefreshPatternPreview();

        // Show/hide the scatter variants editor when the selected group's discipline switches to/from Scatter.
        if (ReferenceEquals(sender, SelectedGroup)
            && e.PropertyName == nameof(GroupDayRowViewModel.EffectiveDiscipline))
            _ = RefreshScatterEditorAsync();
    }

    // Refresh the previews/editors whenever the selected group changes.
    partial void OnSelectedGroupChanged(GroupDayRowViewModel? value)
    {
        RefreshPatternPreview();
        _ = RefreshScatterEditorAsync();
    }

    private void RaiseColumnVisibility()
    {
        OnPropertyChanged(nameof(ShowRequiredCountColumn));
        OnPropertyChanged(nameof(ShowPenaltyColumn));
        ColumnsChanged?.Invoke(this, EventArgs.Empty);
    }

    // True when the current options already represent exactly these days (same count and numbers,
    // in order), so the list can be reused instead of rebuilt.
    private bool SameDays(IReadOnlyList<EventDay> days)
    {
        if (DayOptions.Count != days.Count)
            return false;
        for (var i = 0; i < days.Count; i++)
            if (DayOptions[i].Number != days[i].Number)
                return false;
        return true;
    }

    // Driven by the day ComboBox. Switching the session's day re-raises SessionChanged, which
    // reloads this page; the _syncingDay guard stops LoadAsync's reassignment from re-entering.
    partial void OnSelectedDayChanged(DayOption? value)
    {
        if (_syncingDay || value?.Day is null)
            return;
        if (_session.CurrentDay?.Number == value.Number)
            return;

        _ = SwitchDayAsync(value.Day);
    }

    private async Task SwitchDayAsync(EventDay day)
    {
        await _busy.RunAsync(() => _session.SetCurrentDayAsync(day));
    }

    [RelayCommand]
    private async Task AddGroupAsync()
    {
        if (_session.CurrentDay is null)
            return;

        // Add a blank, named-later row (the user types the name in-grid; the debounced save then
        // persists the rename). Mirrors the control-points "add blank row" flow.
        var row = await _busy.RunAsync(() => _editor.AddGroupToDayAsync(string.Empty));
        Groups.Add(CreateRow(row));
        RaiseColumnVisibility();
    }

    [RelayCommand]
    private async Task PullAllGroupsAsync()
    {
        if (_session.CurrentDay is null)
            return;

        await _busy.RunAsync(() => _editor.PullAllGroupsIntoDayAsync());
        // Reload so the merged set renders in the correct order.
        await LoadAsync();
    }

    [RelayCommand]
    private async Task RecalculateAgeWindowsAsync()
    {
        if (_session.CurrentEvent is null)
            return;

        // Recompute every group's birth-year window from its name (overwriting any hand-edited bounds),
        // then reload so the grid's Min/Max birth-year cells show the new values.
        await _busy.RunAsync(() => _editor.RecalculateGroupAgeWindowsAsync());
        await LoadAsync();
    }

    /// <summary>
    /// Runs the single "Import from XML" action (chosen by the view's file picker): the shared flow
    /// parses the file, shows the two-toggle modal, and imports both control points and groups for
    /// the current day. This page then reloads its groups.
    /// </summary>
    public async Task ImportFromXmlAsync(string xml, string? fileName = null, byte[]? content = null)
    {
        if (await _importFlow.RunAsync(xml, fileName, content))
            await LoadAsync();
    }

    // The grid's delete button binds to this command. A plain click asks for confirmation first;
    // Ctrl+Click passes skipConfirm via DeleteGroupNoConfirm below. The Delete key on a selected row
    // is routed through DeleteSelectedGroupAsync in the view.
    [RelayCommand]
    private Task DeleteGroupAsync(GroupDayRowViewModel? row) => RemoveGroupAsync(row, skipConfirm: false);

    /// <summary>Deletes a row without the confirmation prompt (Ctrl+Click / Ctrl+Delete).</summary>
    public Task DeleteGroupNoConfirmAsync(GroupDayRowViewModel? row) => RemoveGroupAsync(row, skipConfirm: true);

    /// <summary>Deletes the currently selected group (Delete key); confirms unless skipConfirm.</summary>
    public Task DeleteSelectedGroupAsync(bool skipConfirm) => RemoveGroupAsync(SelectedGroup, skipConfirm);

    private async Task RemoveGroupAsync(GroupDayRowViewModel? row, bool skipConfirm)
    {
        if (row is null)
            return;

        var confirmed = false;
        if (!skipConfirm)
        {
            confirmed = await _dialogs.ConfirmAsync(new ConfirmDialogViewModel(
                Localization,
                titleKey: "Groups.Delete.ConfirmTitle",
                messageKey: "Groups.Delete.ConfirmMessage"));
            if (!confirmed)
                return;
        }

        if (_saveTimers.TryGetValue(row.Id, out var cts))
        {
            cts.Cancel();
            _saveTimers.Remove(row.Id);
        }

        // Remove from the grid immediately and run the SQLite delete in the background — the user
        // never waits on the DB for a delete. If the removed row was the focused one, move the
        // selection onto its neighbour so the grid keeps a sensible focus instead of clearing it.
        row.PropertyChanged -= OnRowPropertyChanged;
        if (ReferenceEquals(SelectedGroup, row))
            SelectedGroup = GridSelection.NeighbourAfterRemoval(Groups, row);
        Groups.Remove(row);
        RaiseColumnVisibility();

        var (id, groupId) = (row.Id, row.GroupId);
        _ = Task.Run(() => _editor.RemoveGroupFromDayAsync(id, groupId));

        // The confirmation modal stole keyboard focus to the overlay; pull it back to the grid
        // (now on the new selected row) so focus doesn't end up on the top menu.
        if (confirmed)
            RequestGridFocus();
    }

    // ── Competition-level settings strip: change handlers + debounced save ───────────────────────────

    partial void OnDefaultCourseSetterChanged(string value)
    {
        // Live-propagate the new global course-setter to every row's placeholder so empty cells update
        // immediately (no reload needed). The trimmed value is what a blank cell inherits.
        var placeholder = (value ?? string.Empty).Trim();
        foreach (var row in Groups)
            row.CourseSetterPlaceholder = placeholder;
        QueueInfoSave();
    }

    partial void OnDefaultCourseSetterCategoryChanged(string value)
    {
        var placeholder = (value ?? string.Empty).Trim();
        foreach (var row in Groups)
            row.CourseSetterCategoryPlaceholder = placeholder;
        QueueInfoSave();
    }

    partial void OnSelectedDefaultPointsRuleChanged(PointsRuleOption? value)
    {
        // The competition default rule changed — refresh the per-group "(default: …)" sentinel label so
        // every group combo that inherits the default shows the new name immediately.
        var name = RuleName(value?.Id);
        foreach (var option in _rowPointsRuleOptions)
            if (option.Id is null)
                option.UpdateDefaultName(name);
        QueueInfoSave();
    }

    private void QueueInfoSave()
    {
        if (_suppressInfoSave || _info is null)
            return;

        _infoSaveCts?.Cancel();
        var cts = new CancellationTokenSource();
        _infoSaveCts = cts;
        _ = SaveInfoDebouncedAsync(cts.Token);
    }

    private async Task SaveInfoDebouncedAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(SaveDebounce, token);

            // Snapshot the edited fields onto the loaded info (UI thread) before the SQLite write.
            var info = _info;
            if (info is null)
                return;
            info.CourseSetter = (DefaultCourseSetter ?? string.Empty).Trim();
            info.CourseSetterCategory = (DefaultCourseSetterCategory ?? string.Empty).Trim();
            info.DefaultPointsRuleId = SelectedDefaultPointsRule?.Id;

            await Task.Run(() => _editor.SaveInfoAsync(info, token), token);
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

    // Invoked by a row on every edit (UI thread). Resets that row's debounce timer.
    private void RequestRowSave(GroupDayRowViewModel row)
    {
        if (_saveTimers.TryGetValue(row.Id, out var existing))
            existing.Cancel();

        var cts = new CancellationTokenSource();
        _saveTimers[row.Id] = cts;
        _ = SaveRowDebouncedAsync(row, cts.Token); // fire-and-forget; the UI is never blocked
    }

    private async Task SaveRowDebouncedAsync(GroupDayRowViewModel row, CancellationToken token)
    {
        try
        {
            await Task.Delay(SaveDebounce, token);
            // ToRow() reads VM state, so snapshot it here (UI thread) before offloading the
            // synchronous SQLite write to the pool. Autosave bypasses the busy overlay on purpose.
            var dto = row.ToRow();
            await Task.Run(() => _editor.UpdateGroupDayRowAsync(dto, token), token);
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
        _scatterSaveCts?.Cancel();
    }
}

/// <summary>
/// One editable row in the bottom scatter («розсіювання») variants table: a display <see cref="Code"/> (e.g.
/// "A") and the variant's <see cref="CourseOrder"/> string. Raises <see cref="Changed"/> on any edit so the
/// page can debounce-save the whole set.
/// </summary>
public sealed partial class ScatterVariantRowViewModel : ObservableObject
{
    public ScatterVariantRowViewModel(string code, string courseOrder)
    {
        _code = code;
        _courseOrder = courseOrder;
    }

    /// <summary>Raised whenever the code or course order is edited.</summary>
    public event Action? Changed;

    [ObservableProperty]
    private string _code;

    [ObservableProperty]
    private string _courseOrder;

    partial void OnCodeChanged(string value) => Changed?.Invoke();
    partial void OnCourseOrderChanged(string value) => Changed?.Invoke();
}
