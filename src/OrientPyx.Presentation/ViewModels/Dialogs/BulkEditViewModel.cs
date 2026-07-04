using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.Localization;
using OrientPyx.Presentation.ViewModels.Pages;

namespace OrientPyx.Presentation.ViewModels.Dialogs;

/// <summary>
/// The kind of value editor a bulk-edit field needs. Drives which input control the view shows and
/// which value the result carries.
/// </summary>
public enum BulkEditFieldKind
{
    /// <summary>Free text (e.g. Представник, Оплата): a <see cref="BulkEditViewModel.TextValue"/> text box.</summary>
    Text,

    /// <summary>A true/false flag (e.g. Член ФСОУ): a <see cref="BulkEditViewModel.BoolValue"/> checkbox.</summary>
    Bool,

    /// <summary>The per-day group dropdown (real groups only — no "(none)"/"+ new").</summary>
    Group,

    /// <summary>The competition-level region dropdown (shares the row dropdowns, incl. "+ new").</summary>
    Region,

    /// <summary>The competition-level club dropdown.</summary>
    Club,

    /// <summary>The competition-level ДЮСШ dropdown.</summary>
    Dussh,

    /// <summary>The rank dropdown (stored as text; no "+ new").</summary>
    Rank
}

/// <summary>
/// One selectable target field in the bulk-edit modal: a stable <see cref="Key"/> the page switches on,
/// the <see cref="Kind"/> of value it takes, and a localized <see cref="Label"/> (reusing the table's
/// own column captions so wording stays consistent).
/// </summary>
public sealed class BulkEditFieldOption
{
    public BulkEditFieldOption(string key, BulkEditFieldKind kind, string label)
    {
        Key = key;
        Kind = kind;
        Label = label;
    }

    public string Key { get; }
    public BulkEditFieldKind Kind { get; }
    public string Label { get; }
}

/// <summary>
/// Modal for changing one field across every participant currently shown in the table (the filtered +
/// sorted set the page hands in). The user picks a field, then the matching value editor appears: a
/// dropdown for the list fields (group/region/club/ДЮСШ/rank), a text box for text fields, or a checkbox
/// for the boolean flags. List dropdowns reuse the same option lists (incl. the "+ new" sentinel) as the
/// table's own cells; picking "+ new" raises <see cref="AddRequested"/> so the page runs the existing
/// create flow and re-seeds the dialog. Callers <c>await</c> <see cref="Completion"/> for the chosen
/// field + value, or null on cancel. Mirrors the <see cref="AssignChipsViewModel"/> pattern.
/// </summary>
public sealed partial class BulkEditViewModel : ObservableObject
{
    private readonly TaskCompletionSource<BulkEditResult?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public BulkEditViewModel(
        ILocalizationService localization,
        IReadOnlyList<BulkEditFieldOption> fields,
        IReadOnlyList<GroupOption> groupOptions,
        IReadOnlyList<RegionOption> regionOptions,
        IReadOnlyList<ClubOption> clubOptions,
        IReadOnlyList<DusshOption> dusshOptions,
        IReadOnlyList<RankOption> rankOptions,
        int affectedCount,
        BulkEditFieldOption? initialField = null)
    {
        Localization = localization;
        Fields = new ObservableCollection<BulkEditFieldOption>(fields);
        GroupOptions = groupOptions;
        RegionOptions = new ObservableCollection<RegionOption>(regionOptions);
        ClubOptions = new ObservableCollection<ClubOption>(clubOptions);
        DusshOptions = new ObservableCollection<DusshOption>(dusshOptions);
        RankOptions = rankOptions;
        AffectedCount = affectedCount;

        // Open on the requested field (the focused / right-clicked column) when it's offered, else first.
        _selectedField = initialField is not null && Fields.Contains(initialField)
            ? initialField
            : Fields.Count > 0 ? Fields[0] : null;
        SeedDefaultValue();

        Localization.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Hint));
            OnPropertyChanged(nameof(ValueLabel));
        };
    }

    public ILocalizationService Localization { get; }

    public string Title => Localization.Get("Participants.BulkEdit.Title");

    public string Hint =>
        string.Format(Localization.Get("Participants.BulkEdit.Hint"), AffectedCount);

    public string ValueLabel => Localization.Get("Participants.BulkEdit.Value");

    public string FieldLabel => Localization.Get("Participants.BulkEdit.Field");

    public int AffectedCount { get; }

    /// <summary>The fields the user can target, in display order.</summary>
    public ObservableCollection<BulkEditFieldOption> Fields { get; }

    /// <summary>Group choices for the value dropdown (real groups only — bulk edit never "leaves" a day).</summary>
    public IReadOnlyList<GroupOption> GroupOptions { get; }

    /// <summary>Region choices (shared with the row dropdowns; observable so a "+ new" can append).</summary>
    public ObservableCollection<RegionOption> RegionOptions { get; }
    public ObservableCollection<ClubOption> ClubOptions { get; }
    public ObservableCollection<DusshOption> DusshOptions { get; }

    /// <summary>Rank choices (stored as text; no "+ new").</summary>
    public IReadOnlyList<RankOption> RankOptions { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTextField))]
    [NotifyPropertyChangedFor(nameof(IsBoolField))]
    [NotifyPropertyChangedFor(nameof(IsGroupField))]
    [NotifyPropertyChangedFor(nameof(IsRegionField))]
    [NotifyPropertyChangedFor(nameof(IsClubField))]
    [NotifyPropertyChangedFor(nameof(IsDusshField))]
    [NotifyPropertyChangedFor(nameof(IsRankField))]
    private BulkEditFieldOption? _selectedField;

    public bool IsTextField => SelectedField?.Kind == BulkEditFieldKind.Text;
    public bool IsBoolField => SelectedField?.Kind == BulkEditFieldKind.Bool;
    public bool IsGroupField => SelectedField?.Kind == BulkEditFieldKind.Group;
    public bool IsRegionField => SelectedField?.Kind == BulkEditFieldKind.Region;
    public bool IsClubField => SelectedField?.Kind == BulkEditFieldKind.Club;
    public bool IsDusshField => SelectedField?.Kind == BulkEditFieldKind.Dussh;
    public bool IsRankField => SelectedField?.Kind == BulkEditFieldKind.Rank;

    [ObservableProperty]
    private string _textValue = string.Empty;

    [ObservableProperty]
    private bool _boolValue;

    [ObservableProperty]
    private GroupOption? _selectedGroup;

    [ObservableProperty]
    private RegionOption? _selectedRegion;

    [ObservableProperty]
    private ClubOption? _selectedClub;

    [ObservableProperty]
    private DusshOption? _selectedDussh;

    [ObservableProperty]
    private RankOption? _selectedRank;

    /// <summary>
    /// Raised when the user picks a list field's "+ new" sentinel. The page handles it by running the
    /// existing create flow for that field kind, then calls back into <see cref="ApplyNewRegion"/> /
    /// <see cref="ApplyNewClub"/> / <see cref="ApplyNewDussh"/> with the rebuilt options + new id (or
    /// reverts the selection on cancel via <see cref="RevertList"/>).
    /// </summary>
    public event EventHandler<BulkEditFieldKind>? AddRequested;

    partial void OnSelectedFieldChanged(BulkEditFieldOption? value) => SeedDefaultValue();

    // Seed a sensible default value for the freshly selected field so the editor is never blank/invalid.
    private void SeedDefaultValue()
    {
        switch (SelectedField?.Kind)
        {
            case BulkEditFieldKind.Group:
                SelectedGroup = GroupOptions.FirstOrDefault();
                break;
            case BulkEditFieldKind.Region:
                SelectedRegion = RegionOptions.FirstOrDefault(o => !o.IsAdd);
                break;
            case BulkEditFieldKind.Club:
                SelectedClub = ClubOptions.FirstOrDefault(o => !o.IsAdd);
                break;
            case BulkEditFieldKind.Dussh:
                SelectedDussh = DusshOptions.FirstOrDefault(o => !o.IsAdd);
                break;
            case BulkEditFieldKind.Rank:
                SelectedRank = RankOptions.FirstOrDefault();
                break;
            case BulkEditFieldKind.Text:
                TextValue = string.Empty;
                break;
            case BulkEditFieldKind.Bool:
                BoolValue = false;
                break;
        }
    }

    // The list "+ new" sentinels route to the page instead of staying selected (mirrors the row combos).
    partial void OnSelectedRegionChanged(RegionOption? value)
    {
        if (value is { IsAdd: true })
            AddRequested?.Invoke(this, BulkEditFieldKind.Region);
    }

    partial void OnSelectedClubChanged(ClubOption? value)
    {
        if (value is { IsAdd: true })
            AddRequested?.Invoke(this, BulkEditFieldKind.Club);
    }

    partial void OnSelectedDusshChanged(DusshOption? value)
    {
        if (value is { IsAdd: true })
            AddRequested?.Invoke(this, BulkEditFieldKind.Dussh);
    }

    /// <summary>Rebuilds the region choices after a "+ new" and selects the freshly created region.</summary>
    public void ApplyNewRegion(IReadOnlyList<RegionOption> options, Guid newId)
    {
        RegionOptions.Clear();
        foreach (var o in options)
            RegionOptions.Add(o);
        SelectedRegion = RegionOptions.FirstOrDefault(o => !o.IsAdd && o.Id == newId);
    }

    public void ApplyNewClub(IReadOnlyList<ClubOption> options, Guid newId)
    {
        ClubOptions.Clear();
        foreach (var o in options)
            ClubOptions.Add(o);
        SelectedClub = ClubOptions.FirstOrDefault(o => !o.IsAdd && o.Id == newId);
    }

    public void ApplyNewDussh(IReadOnlyList<DusshOption> options, Guid newId)
    {
        DusshOptions.Clear();
        foreach (var o in options)
            DusshOptions.Add(o);
        SelectedDussh = DusshOptions.FirstOrDefault(o => !o.IsAdd && o.Id == newId);
    }

    /// <summary>Reverts a cancelled "+ new" back to the first real option of that list.</summary>
    public void RevertList(BulkEditFieldKind kind)
    {
        switch (kind)
        {
            case BulkEditFieldKind.Region:
                SelectedRegion = RegionOptions.FirstOrDefault(o => !o.IsAdd);
                break;
            case BulkEditFieldKind.Club:
                SelectedClub = ClubOptions.FirstOrDefault(o => !o.IsAdd);
                break;
            case BulkEditFieldKind.Dussh:
                SelectedDussh = DusshOptions.FirstOrDefault(o => !o.IsAdd);
                break;
        }
    }

    /// <summary>Completes with the chosen field + value on confirm, or null on cancel/close.</summary>
    public Task<BulkEditResult?> Completion => _completion.Task;

    [RelayCommand]
    private void Confirm()
    {
        if (SelectedField is not { } field)
        {
            _completion.TrySetResult(null);
            return;
        }

        var result = field.Kind switch
        {
            BulkEditFieldKind.Text => BulkEditResult.ForText(field.Key, (TextValue ?? string.Empty).Trim()),
            BulkEditFieldKind.Bool => BulkEditResult.ForBool(field.Key, BoolValue),
            BulkEditFieldKind.Group => BulkEditResult.ForId(field.Key, SelectedGroup?.Id),
            BulkEditFieldKind.Region => BulkEditResult.ForId(field.Key, SelectedRegion?.Id),
            BulkEditFieldKind.Club => BulkEditResult.ForId(field.Key, SelectedClub?.Id),
            BulkEditFieldKind.Dussh => BulkEditResult.ForId(field.Key, SelectedDussh?.Id),
            BulkEditFieldKind.Rank => BulkEditResult.ForText(field.Key, SelectedRank?.Value ?? string.Empty),
            _ => null
        };

        _completion.TrySetResult(result);
    }

    [RelayCommand]
    private void Cancel() => _completion.TrySetResult(null);
}

/// <summary>
/// The confirmed bulk edit: the target field <see cref="Key"/> plus exactly one of the typed values
/// depending on the field kind. Id-backed fields (group/region/club/ДЮСШ) carry <see cref="Id"/> (null =
/// "(none)"); text/rank carry <see cref="Text"/>; bool fields carry <see cref="Bool"/>.
/// </summary>
public sealed class BulkEditResult
{
    private BulkEditResult(string key, Guid? id, string? text, bool? @bool)
    {
        Key = key;
        Id = id;
        Text = text;
        Bool = @bool;
    }

    public string Key { get; }
    public Guid? Id { get; }
    public string? Text { get; }
    public bool? Bool { get; }

    public static BulkEditResult ForId(string key, Guid? id) => new(key, id, null, null);
    public static BulkEditResult ForText(string key, string text) => new(key, null, text, null);
    public static BulkEditResult ForBool(string key, bool value) => new(key, null, null, value);
}
