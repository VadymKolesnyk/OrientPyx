using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Localization;

namespace OrientPyx.Presentation.ViewModels.Dialogs;

/// <summary>
/// The all-days import's confirmation: three blocks — athletes the file adds, athletes it updates (with the
/// fields that change) and athletes in the database the file no longer lists — each with «select all / none».
/// A ticked row in the first two blocks is imported (unticked ones are left out); a ticked row in the third is
/// deleted from the database (unticked by default: nothing is removed unless asked). Completes with the
/// decision, or null on cancel.
/// </summary>
public sealed partial class ImportPreviewViewModel : ObservableObject
{
    private readonly TaskCompletionSource<ImportPreviewDecision?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ImportPreviewViewModel(ILocalizationService localization, ImportPreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        Localization = localization;

        Added = new ObservableCollection<ImportPreviewItem>(preview.Added.Select(a =>
            new ImportPreviewItem(a.RowIndex, null, a.FullName, a.Group,
                Details(a.BirthYear, a.Club, a.Region, a.DayNumbers), [], isChecked: true, OnItemChanged)));
        Updated = new ObservableCollection<ImportPreviewItem>(preview.Updated.Select(u =>
            new ImportPreviewItem(u.RowIndex, u.ParticipantId, u.FullName, u.Group,
                u.Changes.Count == 0 ? Localization.Get("ParticipantsImport.Preview.NoChanges") : string.Empty,
                u.Changes.Select(ChangeText).ToList(), isChecked: true, OnItemChanged)));
        Missing = new ObservableCollection<ImportPreviewItem>(preview.Missing.Select(m =>
            new ImportPreviewItem(-1, m.ParticipantId, m.FullName, m.Groups,
                Details(m.BirthYear, m.Club, m.Region, m.DayNumbers), [], isChecked: false, OnItemChanged)));

        Localization.PropertyChanged += (_, _) => OnPropertyChanged(string.Empty);
    }

    public ILocalizationService Localization { get; }

    public ObservableCollection<ImportPreviewItem> Added { get; }

    public ObservableCollection<ImportPreviewItem> Updated { get; }

    public ObservableCollection<ImportPreviewItem> Missing { get; }

    public bool HasAdded => Added.Count > 0;

    public bool HasUpdated => Updated.Count > 0;

    public bool HasMissing => Missing.Count > 0;

    public string Title => Localization.Get("ParticipantsImport.Preview.Title");

    public string Message => Localization.Get("ParticipantsImport.Preview.Message");

    public string AddedHeader => Header("ParticipantsImport.Preview.Added", Added);

    public string UpdatedHeader => Header("ParticipantsImport.Preview.Updated", Updated);

    public string MissingHeader => Header("ParticipantsImport.Preview.Missing", Missing);

    /// <summary>«Додати: 3 · Оновити: 5 · Видалити: 1».</summary>
    public string Summary => string.Format(
        CultureInfo.CurrentCulture,
        Localization.Get("ParticipantsImport.Preview.Summary"),
        Added.Count(i => i.IsChecked),
        Updated.Count(i => i.IsChecked),
        Missing.Count(i => i.IsChecked));

    public Task<ImportPreviewDecision?> Completion => _completion.Task;

    private string Header(string key, ObservableCollection<ImportPreviewItem> items) =>
        string.Format(CultureInfo.CurrentCulture, Localization.Get(key), items.Count(i => i.IsChecked), items.Count);

    private void OnItemChanged()
    {
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(AddedHeader));
        OnPropertyChanged(nameof(UpdatedHeader));
        OnPropertyChanged(nameof(MissingHeader));
    }

    // «2008 · Клуб · Регіон · дні: 1, 2» — the parts that are present, joined.
    private string Details(int? birthYear, string club, string region, IReadOnlyList<int> days)
    {
        var parts = new List<string>();
        if (birthYear is { } year)
            parts.Add(string.Format(CultureInfo.CurrentCulture, Localization.Get("ParticipantsImport.Duplicates.BirthYear"), year));
        if (!string.IsNullOrWhiteSpace(club))
            parts.Add(club.Trim());
        if (!string.IsNullOrWhiteSpace(region))
            parts.Add(region.Trim());
        if (days.Count > 0)
            parts.Add(string.Format(CultureInfo.CurrentCulture, Localization.Get("ParticipantsImport.Preview.Days"),
                string.Join(", ", days)));
        return string.Join(" · ", parts);
    }

    // «Клуб: Старий → Новий», «Чіп (день 2): — → 8001234», «День 2: додається».
    private string ChangeText(ImportPreviewChange change)
    {
        if (change.Field == "Day")
            return string.Format(CultureInfo.CurrentCulture, Localization.Get("ParticipantsImport.Preview.NewDay"), change.DayNumber);

        var label = change.Field.StartsWith(CustomField.ColumnPrefix, StringComparison.Ordinal)
            ? change.Field[CustomField.ColumnPrefix.Length..]
            : Localization.Get("ParticipantsImport.Preview.Field." + change.Field);
        if (change.DayNumber is { } day)
            label = string.Format(CultureInfo.CurrentCulture, Localization.Get("ParticipantsImport.Preview.DayField"), label, day);
        var before = change.Before.Length == 0 ? "—" : change.Before;
        var after = change.After.Length == 0 ? "—" : change.After;
        return $"{label}: {before} → {after}";
    }

    [RelayCommand]
    private void SelectAllAdded() => Set(Added, true);

    [RelayCommand]
    private void DeselectAllAdded() => Set(Added, false);

    [RelayCommand]
    private void SelectAllUpdated() => Set(Updated, true);

    [RelayCommand]
    private void DeselectAllUpdated() => Set(Updated, false);

    [RelayCommand]
    private void SelectAllMissing() => Set(Missing, true);

    [RelayCommand]
    private void DeselectAllMissing() => Set(Missing, false);

    private static void Set(ObservableCollection<ImportPreviewItem> items, bool value)
    {
        foreach (var item in items)
            item.IsChecked = value;
    }

    [RelayCommand]
    private void Confirm()
    {
        var excluded = Added.Concat(Updated)
            .Where(i => !i.IsChecked)
            .Select(i => i.RowIndex)
            .ToHashSet();
        var delete = Missing.Where(i => i.IsChecked).Select(i => i.ParticipantId!.Value).ToList();
        _completion.TrySetResult(new ImportPreviewDecision(excluded, delete));
    }

    [RelayCommand]
    private void Cancel() => _completion.TrySetResult(null);
}

/// <summary>One row of a preview block: a file row (added / updated) or a database athlete (missing).</summary>
public sealed partial class ImportPreviewItem : ObservableObject
{
    private readonly Action _changed;

    public ImportPreviewItem(
        int rowIndex,
        Guid? participantId,
        string fullName,
        string group,
        string details,
        IReadOnlyList<string> changes,
        bool isChecked,
        Action changed)
    {
        RowIndex = rowIndex;
        ParticipantId = participantId;
        FullName = fullName;
        Group = group;
        Details = details;
        Changes = changes;
        _isChecked = isChecked;
        _changed = changed;
    }

    /// <summary>The file row (−1 for a missing athlete).</summary>
    public int RowIndex { get; }

    public Guid? ParticipantId { get; }

    public string FullName { get; }

    public string Group { get; }

    public bool HasGroup => Group.Length > 0;

    public string Details { get; }

    public bool HasDetails => Details.Length > 0;

    /// <summary>The field changes, one line each (updated rows only).</summary>
    public IReadOnlyList<string> Changes { get; }

    public bool HasChanges => Changes.Count > 0;

    [ObservableProperty]
    private bool _isChecked;

    partial void OnIsCheckedChanged(bool value) => _changed();
}

/// <summary>The user's choices: file rows to leave out, and database athletes to delete.</summary>
public sealed record ImportPreviewDecision(IReadOnlySet<int> ExcludedRows, IReadOnlyList<Guid> DeleteParticipantIds);
