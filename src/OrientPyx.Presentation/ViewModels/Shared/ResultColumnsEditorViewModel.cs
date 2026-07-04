using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Localization;

namespace OrientPyx.Presentation.ViewModels.Shared;

/// <summary>
/// A small, reusable editor for a <see cref="ResultColumnSelection"/>: the full column catalogue presented as
/// an ordered list of check-boxes the user can toggle and move up/down. Shared by the online-results and
/// on-screen-monitor configuration so both pick columns the same way. Holds no persistence — the host VM
/// loads it from a selection and reads <see cref="BuildSelection"/> back when saving.
/// </summary>
public sealed partial class ResultColumnsEditorViewModel : ObservableObject
{
    private readonly ILocalizationService _localization;

    public ResultColumnsEditorViewModel(ILocalizationService localization)
    {
        _localization = localization;
        Load(ResultColumnSelection.Default);
        _localization.PropertyChanged += (_, _) => RefreshLabels();
    }

    /// <summary>The column rows, in display order. Selected ones come first (in their saved order), then the
    /// rest of the catalogue unchecked.</summary>
    public ObservableCollection<ResultColumnItem> Columns { get; } = [];

    /// <summary>Raised whenever the set or order of columns changes, so the host can mark itself dirty / refresh a preview.</summary>
    public event EventHandler? Changed;

    /// <summary>Resets the editor to reflect the given selection: its columns first (checked, in order), then
    /// the remaining catalogue columns (unchecked) in their natural order.</summary>
    public void Load(ResultColumnSelection? selection)
    {
        var sel = selection ?? ResultColumnSelection.Default;
        var chosen = sel.Resolve();
        var chosenKeys = chosen.Select(d => d.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Columns.Clear();
        foreach (var def in chosen)
            Columns.Add(MakeItem(def, isChecked: true));
        foreach (var def in ResultColumnDef.All.Where(d => !chosenKeys.Contains(d.Key)))
            Columns.Add(MakeItem(def, isChecked: false));
    }

    /// <summary>The current selection: the checked columns, in their current order.</summary>
    public ResultColumnSelection BuildSelection() =>
        new(Columns.Where(c => c.IsChecked).Select(c => c.Key).ToList());

    private ResultColumnItem MakeItem(ResultColumnDef def, bool isChecked)
    {
        var item = new ResultColumnItem(def, _localization.Get(def.LabelKey)) { IsChecked = isChecked };
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ResultColumnItem.IsChecked))
                Changed?.Invoke(this, EventArgs.Empty);
        };
        return item;
    }

    private void RefreshLabels()
    {
        foreach (var item in Columns)
            item.Label = _localization.Get(item.Definition.LabelKey);
    }

    [RelayCommand]
    private void MoveUp(ResultColumnItem? item)
    {
        if (item is null)
            return;
        var i = Columns.IndexOf(item);
        if (i > 0)
        {
            Columns.Move(i, i - 1);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    [RelayCommand]
    private void MoveDown(ResultColumnItem? item)
    {
        if (item is null)
            return;
        var i = Columns.IndexOf(item);
        if (i >= 0 && i < Columns.Count - 1)
        {
            Columns.Move(i, i + 1);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Moves <paramref name="dragged"/> to sit just before/after <paramref name="target"/>, for
    /// drag-and-drop reordering of the column rows. No-op when either is missing or it's a self-drop.</summary>
    public void MoveColumn(ResultColumnItem? dragged, ResultColumnItem? target, bool after)
    {
        if (dragged is null || target is null || ReferenceEquals(dragged, target))
            return;

        var from = Columns.IndexOf(dragged);
        var to = Columns.IndexOf(target);
        if (from < 0 || to < 0)
            return;

        // Insert before/after the target; account for the removal shifting indices left when moving down.
        var insert = after ? to + 1 : to;
        if (from < insert)
            insert--;
        insert = Math.Clamp(insert, 0, Columns.Count - 1);
        if (insert == from)
            return;

        Columns.Move(from, insert);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>One row in the <see cref="ResultColumnsEditorViewModel"/>: a column, its (live-localized) label, and whether it is shown.</summary>
public sealed partial class ResultColumnItem : ObservableObject
{
    public ResultColumnItem(ResultColumnDef definition, string label)
    {
        Definition = definition;
        _label = label;
    }

    public ResultColumnDef Definition { get; }

    public string Key => Definition.Key;

    [ObservableProperty]
    private bool _isChecked;

    [ObservableProperty]
    private string _label;
}
