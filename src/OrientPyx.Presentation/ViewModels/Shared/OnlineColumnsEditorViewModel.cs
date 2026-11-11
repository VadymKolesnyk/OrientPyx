using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Localization;

namespace OrientPyx.Presentation.ViewModels.Shared;

/// <summary>
/// The editor for an <see cref="OnlineDisplayConfig"/>: the full column catalogue — plus the day's extra columns
/// (plugin columns, additional fields; see <see cref="SetExtraColumns"/>) — as an ordered list where each
/// row carries TWO visibility toggles — large screen (venue / desktop) and small screen (phone) — plus the two
/// «Статус/DSQ»-column split flags. Unlike the flat <see cref="ResultColumnsEditorViewModel"/> the monitor uses,
/// a column here can be shown on the big screen yet hidden on a phone. Reorder by drag (from the preview header,
/// via <see cref="MoveColumn"/>) or the ▲/▼ buttons. Holds no persistence — the host loads it from a config and
/// reads <see cref="BuildConfig"/> back on save.
/// </summary>
public sealed partial class OnlineColumnsEditorViewModel : ObservableObject
{
    private readonly ILocalizationService _localization;

    // Suppresses the Changed event while (re)loading the whole list, so a bulk Load fires at most one refresh.
    private bool _loading;

    // The day's extra columns (labels are localization keys / field labels), and the config's entries for extras the
    // day does not have — kept unseen and saved back unchanged, so switching days loses no choice.
    private IReadOnlyList<OnlineExtraColumn> _extras = [];
    private IReadOnlyList<OnlineColumnConfig> _unavailable = [];

    public OnlineColumnsEditorViewModel(ILocalizationService localization)
    {
        _localization = localization;
        Load(OnlineDisplayConfig.Default);
        _localization.PropertyChanged += (_, _) => RefreshLabels();
    }

    /// <summary>The column rows, in display order.</summary>
    public ObservableCollection<OnlineColumnItem> Columns { get; } = [];

    /// <summary>Show a separate «Статус/DSQ» column on the large screen.</summary>
    [ObservableProperty]
    private bool _separateDsqLarge = true;

    /// <summary>Show a separate «Статус/DSQ» column on the small (phone) screen.</summary>
    [ObservableProperty]
    private bool _separateDsqSmall;

    /// <summary>Raised whenever the columns' order/visibility or a DSQ flag changes, so the host marks itself
    /// dirty and refreshes the preview.</summary>
    public event EventHandler? Changed;

    partial void OnSeparateDsqLargeChanged(bool value) => RaiseChanged();
    partial void OnSeparateDsqSmallChanged(bool value) => RaiseChanged();

    /// <summary>Resets the editor to reflect the given config: its columns in order (each with its own lg/sm),
    /// then any remaining catalogue columns appended hidden — so a column added since the config was saved shows
    /// up as "off" rather than vanishing.</summary>
    public void Load(OnlineDisplayConfig? config)
    {
        _loading = true;
        try
        {
            var cfg = config ?? OnlineDisplayConfig.Default;
            SeparateDsqLarge = cfg.SeparateDsqLg;
            SeparateDsqSmall = cfg.SeparateDsqSm;

            Columns.Clear();
            foreach (var c in cfg.Resolve(_extras))
                Columns.Add(MakeItem(c.Def, c.Lg, c.Sm));
            _unavailable = cfg.Unresolved(_extras);
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Offers the day's extra columns (plugin columns, additional fields): one the layout already has keeps
    /// its place and toggles; a new plugin column is appended shown on the large screen, a new field hidden — the
    /// same rule the publisher applies, so the list shows what the site gets.</summary>
    public void SetExtraColumns(IReadOnlyList<OnlineExtraColumn> extras)
    {
        if (_extras.Select(e => (e.Key, e.Label)).SequenceEqual(extras.Select(e => (e.Key, e.Label))))
            return;
        var current = BuildConfig();
        _extras = extras;
        Load(current);
    }

    /// <summary>The current config: every column in order with its lg/sm flags (then the extras the day lacks, as
    /// they were), plus the DSQ split toggles.</summary>
    public OnlineDisplayConfig BuildConfig() =>
        new(Columns.Select(c => new OnlineColumnConfig(c.Key, c.Lg, c.Sm)).Concat(_unavailable).ToList(),
            SeparateDsqLarge, SeparateDsqSmall);

    private OnlineColumnItem MakeItem(ResultColumnInfo def, bool lg, bool sm)
    {
        var item = new OnlineColumnItem(def, _localization.Get(def.LabelKey)) { Lg = lg, Sm = sm };
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(OnlineColumnItem.Lg) or nameof(OnlineColumnItem.Sm))
                RaiseChanged();
        };
        return item;
    }

    private void RefreshLabels()
    {
        foreach (var item in Columns)
            item.Label = _localization.Get(item.Definition.LabelKey);
    }

    private void RaiseChanged()
    {
        if (!_loading)
            Changed?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void MoveUp(OnlineColumnItem? item)
    {
        if (item is null)
            return;
        var i = Columns.IndexOf(item);
        if (i > 0)
        {
            Columns.Move(i, i - 1);
            RaiseChanged();
        }
    }

    [RelayCommand]
    private void MoveDown(OnlineColumnItem? item)
    {
        if (item is null)
            return;
        var i = Columns.IndexOf(item);
        if (i >= 0 && i < Columns.Count - 1)
        {
            Columns.Move(i, i + 1);
            RaiseChanged();
        }
    }

    /// <summary>Moves <paramref name="dragged"/> to sit just before/after <paramref name="target"/>, for
    /// drag-and-drop reordering of the column rows. No-op when either is missing or it's a self-drop. (Same index
    /// math as <see cref="ResultColumnsEditorViewModel.MoveColumn"/>.)</summary>
    public void MoveColumn(OnlineColumnItem? dragged, OnlineColumnItem? target, bool after)
    {
        if (dragged is null || target is null || ReferenceEquals(dragged, target))
            return;

        var from = Columns.IndexOf(dragged);
        var to = Columns.IndexOf(target);
        if (from < 0 || to < 0)
            return;

        var insert = after ? to + 1 : to;
        if (from < insert)
            insert--;
        insert = Math.Clamp(insert, 0, Columns.Count - 1);
        if (insert == from)
            return;

        Columns.Move(from, insert);
        RaiseChanged();
    }
}

/// <summary>One row in the <see cref="OnlineColumnsEditorViewModel"/>: a column, its (live-localized) label, and
/// whether it is shown on the large (<see cref="Lg"/>) and small (<see cref="Sm"/>) screen.</summary>
public sealed partial class OnlineColumnItem : ObservableObject
{
    public OnlineColumnItem(ResultColumnInfo definition, string label)
    {
        Definition = definition;
        _label = label;
    }

    public ResultColumnInfo Definition { get; }

    public string Key => Definition.Key;

    public ResultColumn Column => Definition.Column;

    [ObservableProperty]
    private bool _lg;

    [ObservableProperty]
    private bool _sm;

    [ObservableProperty]
    private string _label;
}
