using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Localization;
using OrientPyx.Presentation.Services;
using OrientPyx.Presentation.ViewModels.Dialogs;

namespace OrientPyx.Presentation.ViewModels.Pages;

/// <summary>A formula variable shown in the insert palette: the token to insert + a localized description.</summary>
public sealed record PointsVariableItem(string Token, string Description);

/// <summary>
/// The application-level points ("Очки") settings page: a master-detail editor for the rules that award
/// ranking points. The left list holds every rule; the right pane edits the selected one. A rule is
/// either a <b>placement table</b> (place→points grid) or a <b>formula</b> (expression over the allowed
/// variables, e.g. <c>100*(2-T_у/T_л)</c>). Rules live in the app database (shared across competitions),
/// so this page talks to <see cref="IAppStore"/> directly. Edits auto-save (debounced) per rule.
///
/// The page only defines the rule catalogue; linking a rule to days/groups for scoring comes later.
/// </summary>
public sealed partial class PointsViewModel : PageViewModelBase
{
    private static readonly TimeSpan SaveDebounce = TimeSpan.FromMilliseconds(600);

    private readonly IAppStore _appStore;
    private readonly IBusyService _busy;
    private readonly IDialogService _dialogs;

    // Debounced save timers keyed by rule id (the detail editor and rename both route through here).
    private readonly Dictionary<Guid, CancellationTokenSource> _saveTimers = new();

    // Guards detail-editor change handlers while we load a freshly selected rule into the pane.
    private bool _loadingDetail;

    public PointsViewModel(
        ILocalizationService localization,
        IAppStore appStore,
        IBusyService busy,
        IDialogService dialogs,
        ITableLayoutStore layoutStore)
        : base(localization)
    {
        LayoutStore = layoutStore;
        _appStore = appStore;
        _busy = busy;
        _dialogs = dialogs;
        Variables = PointsFormula.Variables
            .Select(v => new PointsVariableItem(v.Token, Localization.Get(v.DescriptionKey)))
            .ToList();
    }

    /// <summary>Per-competition table-view store; persists this page's tables' column order/width/visibility.</summary>
    public ITableLayoutStore LayoutStore { get; }

    public override string NavKey => "Nav.Points";
    public override string TitleKey => "Page.Points.Title";
    public override string TextKey => "Page.Points.Text";

    public ObservableCollection<PointsRuleListItemViewModel> Rules { get; } = [];

    /// <summary>The rule currently being edited; selecting one loads it into the detail pane.</summary>
    [ObservableProperty]
    private PointsRuleListItemViewModel? _selectedRule;

    // ── Detail pane: shared ────────────────────────────────────────────────────────────────────────

    /// <summary>Whether a rule is selected (the detail pane is shown).</summary>
    public bool HasSelection => SelectedRule is not null;

    /// <summary>True when the selected rule is a placement table.</summary>
    public bool IsTableEditor => SelectedRule?.Kind == PointsRuleKind.Table;

    /// <summary>True when the selected rule is a formula.</summary>
    public bool IsFormulaEditor => SelectedRule?.Kind == PointsRuleKind.Formula;

    /// <summary>The editable name of the selected rule (shown above the detail editor).</summary>
    [ObservableProperty]
    private string _editName = string.Empty;

    // ── Detail pane: table editor ──────────────────────────────────────────────────────────────────

    /// <summary>The place→points rows for a table rule (index 0 = 1st place).</summary>
    public ObservableCollection<PointsTableRowViewModel> TableRows { get; } = [];

    // ── Detail pane: formula editor ────────────────────────────────────────────────────────────────

    /// <summary>The formula expression text for a formula rule.</summary>
    [ObservableProperty]
    private string _formulaText = string.Empty;

    /// <summary>Whether the current formula text parses (drives the validity badge).</summary>
    [ObservableProperty]
    private bool _formulaValid;

    /// <summary>The variables a formula may reference (for the insert palette), with localized descriptions.</summary>
    public IReadOnlyList<PointsVariableItem> Variables { get; }

    /// <summary>
    /// Where the formula caret currently sits, so a palette insert lands at the cursor. Bound to the
    /// editor's caret index by the View; defaults to the end.
    /// </summary>
    [ObservableProperty]
    private int _formulaCaret;

    public async Task LoadAsync()
    {
        CancelAllTimers();
        var rules = await _busy.RunAsync(() => _appStore.GetPointsRulesAsync());

        var previousId = SelectedRule?.Id;
        Rules.Clear();
        foreach (var rule in rules)
            Rules.Add(new PointsRuleListItemViewModel(rule, Localization));

        // Restore the prior selection if it still exists, else select the first rule.
        SelectedRule = Rules.FirstOrDefault(r => r.Id == previousId) ?? Rules.FirstOrDefault();
    }

    partial void OnSelectedRuleChanged(PointsRuleListItemViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(IsTableEditor));
        OnPropertyChanged(nameof(IsFormulaEditor));
        _ = LoadDetailAsync(value);
    }

    private async Task LoadDetailAsync(PointsRuleListItemViewModel? item)
    {
        TableRows.Clear();
        if (item is null)
        {
            _loadingDetail = true;
            EditName = string.Empty;
            FormulaText = string.Empty;
            FormulaValid = false;
            _loadingDetail = false;
            return;
        }

        // Re-read the full rule (the list item only carries name + kind).
        var rules = await _appStore.GetPointsRulesAsync();
        var rule = rules.FirstOrDefault(r => r.Id == item.Id);
        if (rule is null)
            return;

        _loadingDetail = true;
        EditName = rule.Name;

        if (rule.Kind == PointsRuleKind.Table)
        {
            var values = PointsTable.Parse(rule.TableJson);
            for (var i = 0; i < values.Count; i++)
                TableRows.Add(new PointsTableRowViewModel(i + 1, values[i], OnTableEdited));
            FormulaText = string.Empty;
        }
        else
        {
            FormulaText = rule.Formula ?? string.Empty;
            FormulaValid = PointsFormula.TryValidate(FormulaText, out _);
        }

        _loadingDetail = false;
    }

    // ── Name / table / formula change handlers ─────────────────────────────────────────────────────

    partial void OnEditNameChanged(string value)
    {
        if (_loadingDetail || SelectedRule is null)
            return;

        SelectedRule.Name = value;
        QueueSave();
    }

    private void OnTableEdited()
    {
        if (!_loadingDetail)
            QueueSave();
    }

    partial void OnFormulaTextChanged(string value)
    {
        FormulaValid = PointsFormula.TryValidate(value, out _);
        if (!_loadingDetail)
            QueueSave();
    }

    // ── Commands ───────────────────────────────────────────────────────────────────────────────────

    [RelayCommand]
    private Task AddTableRuleAsync() => AddRuleAsync(PointsRuleKind.Table);

    [RelayCommand]
    private Task AddFormulaRuleAsync() => AddRuleAsync(PointsRuleKind.Formula);

    private async Task AddRuleAsync(PointsRuleKind kind)
    {
        var rule = await _busy.RunAsync(() => _appStore.AddPointsRuleAsync(kind));
        var item = new PointsRuleListItemViewModel(rule, Localization);
        Rules.Add(item);
        SelectedRule = item;
    }

    /// <summary>Appends a place row to the table editor (next place number, 0 points).</summary>
    [RelayCommand]
    private void AddPlace()
    {
        if (!IsTableEditor)
            return;
        TableRows.Add(new PointsTableRowViewModel(TableRows.Count + 1, 0m, OnTableEdited));
        QueueSave();
    }

    /// <summary>Removes the last place row from the table editor.</summary>
    [RelayCommand]
    private void RemovePlace()
    {
        if (!IsTableEditor || TableRows.Count == 0)
            return;
        TableRows.RemoveAt(TableRows.Count - 1);
        QueueSave();
    }

    /// <summary>Inserts a variable token into the formula text at the caret.</summary>
    [RelayCommand]
    private void InsertVariable(string? token)
    {
        if (token is null || !IsFormulaEditor)
            return;

        var text = FormulaText ?? string.Empty;
        var caret = Math.Clamp(FormulaCaret, 0, text.Length);
        FormulaText = text[..caret] + token + text[caret..];
        FormulaCaret = caret + token.Length;
    }

    [RelayCommand]
    private async Task DeleteRuleAsync()
    {
        var item = SelectedRule;
        if (item is null)
            return;

        var confirmed = await _dialogs.ConfirmAsync(new ConfirmDialogViewModel(
            Localization,
            titleKey: "Points.Delete.ConfirmTitle",
            messageKey: "Points.Delete.ConfirmMessage"));
        if (!confirmed)
            return;

        if (_saveTimers.TryGetValue(item.Id, out var cts))
        {
            cts.Cancel();
            _saveTimers.Remove(item.Id);
        }

        var neighbour = GridSelection.NeighbourAfterRemoval(Rules, item);
        Rules.Remove(item);
        SelectedRule = neighbour;

        var id = item.Id;
        _ = Task.Run(() => _appStore.DeletePointsRuleAsync(id));
    }

    // ── Debounced save ─────────────────────────────────────────────────────────────────────────────

    private void QueueSave()
    {
        var item = SelectedRule;
        if (item is null)
            return;

        if (_saveTimers.TryGetValue(item.Id, out var existing))
            existing.Cancel();

        var cts = new CancellationTokenSource();
        _saveTimers[item.Id] = cts;
        _ = SaveDebouncedAsync(BuildEntity(item), cts.Token);
    }

    private PointsRule BuildEntity(PointsRuleListItemViewModel item) => new()
    {
        Id = item.Id,
        Name = (EditName ?? string.Empty).Trim(),
        Kind = item.Kind,
        TableJson = item.Kind == PointsRuleKind.Table
            ? PointsTable.Serialize(TableRows.Select(r => r.Points))
            : null,
        Formula = item.Kind == PointsRuleKind.Formula ? (FormulaText ?? string.Empty) : null,
    };

    private async Task SaveDebouncedAsync(PointsRule entity, CancellationToken token)
    {
        try
        {
            await Task.Delay(SaveDebounce, token);
            await Task.Run(() => _appStore.UpdatePointsRuleAsync(entity, token), token);
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
