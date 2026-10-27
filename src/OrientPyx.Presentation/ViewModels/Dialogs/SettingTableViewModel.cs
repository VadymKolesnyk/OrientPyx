using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.BusinessLogic.Disciplines.Plugins;
using OrientPyx.Localization;

namespace OrientPyx.Presentation.ViewModels.Dialogs;

/// <summary>
/// «Редагувати…» on a table setting (e.g. the coefficient table of «Зважена естафета»): the rows in a sheet, one
/// text cell per column; add / delete rows, «Скинути» back to the discipline's default. Completes with the edited
/// table, or null on cancel. Shown in place of the settings dialog, which comes back afterwards.
/// </summary>
public sealed partial class SettingTableViewModel : ObservableObject
{
    private readonly TaskCompletionSource<TableValue?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SettingDef _def;

    public SettingTableViewModel(ILocalizationService localization, string? pluginId, SettingDef def, TableValue value)
    {
        Localization = localization;
        _def = def;
        Title = localization.Get(pluginId is null ? def.Label : PluginStrings.Key(pluginId, def.Label));
        Columns = def.Columns
            .Select(c => new SettingTableColumn(
                c.Key, pluginId is null ? c.Label : PluginStrings.Key(pluginId, c.Label), c.Kind == TableColumnKind.Number))
            .ToList();
        Load(value);
    }

    public ILocalizationService Localization { get; }

    public string Title { get; }

    /// <summary>The table's columns; the header text is a localization key (plugin keys resolve through the overlay).</summary>
    public IReadOnlyList<SettingTableColumn> Columns { get; }

    public ObservableCollection<SettingTableRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    private SettingTableRowViewModel? _selectedRow;

    public Task<TableValue?> Completion => _completion.Task;

    private void Load(TableValue value)
    {
        Rows.Clear();
        foreach (var row in value.Rows)
            Rows.Add(new SettingTableRowViewModel(Columns.Select(c => row.TryGetValue(c.Key, out var v) ? v ?? string.Empty : string.Empty)));
    }

    [RelayCommand]
    private void AddRow()
    {
        var row = new SettingTableRowViewModel(Columns.Select(_ => string.Empty));
        var at = SelectedRow is { } selected ? Rows.IndexOf(selected) + 1 : Rows.Count;
        Rows.Insert(Math.Clamp(at, 0, Rows.Count), row);
        SelectedRow = row;
    }

    /// <summary>Removes a row (the sheet's Delete key and the row's delete button).</summary>
    public void DeleteRow(object? row)
    {
        if (row is SettingTableRowViewModel vm)
            Rows.Remove(vm);
    }

    [RelayCommand]
    private void ResetToDefault() => Load(_def.DefaultValue as TableValue ?? TableValue.Empty);

    [RelayCommand]
    private void Confirm() =>
        _completion.TrySetResult(new TableValue(Rows
            .Where(r => r.Cells.Any(c => !string.IsNullOrWhiteSpace(c.Text)))
            .Select(r =>
            {
                var cells = new Dictionary<string, string?>(StringComparer.Ordinal);
                for (var i = 0; i < Columns.Count; i++)
                    cells[Columns[i].Key] = r.Cells[i].Text.Trim();
                return (IReadOnlyDictionary<string, string?>)cells;
            })));

    [RelayCommand]
    private void Cancel() => _completion.TrySetResult(null);
}

/// <summary>One column of the table editor.</summary>
public sealed record SettingTableColumn(string Key, string LabelKey, bool IsNumber);

/// <summary>One row of the table editor: a cell per column, bound as <c>Cells[i].Text</c>.</summary>
public sealed class SettingTableRowViewModel
{
    public SettingTableRowViewModel(IEnumerable<string> values) =>
        Cells = values.Select(v => new SettingTableCellViewModel { Text = v }).ToList();

    public IReadOnlyList<SettingTableCellViewModel> Cells { get; }
}

public sealed partial class SettingTableCellViewModel : ObservableObject
{
    [ObservableProperty]
    private string _text = string.Empty;
}
