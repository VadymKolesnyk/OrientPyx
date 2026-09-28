using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Localization;

namespace OrientPyx.Presentation.ViewModels.Dialogs;

/// <summary>
/// «Можливі дублікати учасників»: shown by a current-day-only import when some rows would become new
/// participants although a namesake already ran in the same group on another day. One card per such row —
/// the file's details on top, then a radio per existing candidate («та сама людина — об'єднати») plus «новий
/// учасник». A choice is pre-selected (merge when the known details agree, new otherwise) so a long list can
/// be confirmed quickly; «Усіх об'єднати» / «Усі нові» flip everything. <see cref="Completion"/> yields
/// row index → participant id for the merges, or null on cancel.
/// </summary>
public sealed partial class ImportDuplicatesViewModel : ObservableObject
{
    private readonly TaskCompletionSource<IReadOnlyDictionary<int, Guid>?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly int _dayNumber;
    private readonly ParticipantLinkField _linkField;

    public ImportDuplicatesViewModel(
        ILocalizationService localization,
        int dayNumber,
        ParticipantLinkField linkField,
        IReadOnlyList<ImportDuplicateCase> cases)
    {
        Localization = localization;
        _dayNumber = dayNumber;
        _linkField = linkField;
        Cases = new ObservableCollection<ImportDuplicateItem>(
            cases.Select(c => new ImportDuplicateItem(localization, c, OnChoiceChanged)));

        Localization.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Message));
            OnPropertyChanged(nameof(Summary));
            foreach (var item in Cases)
                item.Refresh();
        };
    }

    public ILocalizationService Localization { get; }

    public ObservableCollection<ImportDuplicateItem> Cases { get; }

    public string Title => Localization.Get("ParticipantsImport.Duplicates.Title");

    public string Message => string.Format(
        Localization.Get("ParticipantsImport.Duplicates.Message"),
        Localization.Get(_linkField == ParticipantLinkField.FullName
            ? "ParticipantsImport.Scope.LinkName"
            : "ParticipantsImport.Scope.LinkFsou"),
        _dayNumber);

    public string Summary
    {
        get
        {
            var merges = Cases.Count(c => c.MergeTarget is not null);
            return string.Format(Localization.Get("ParticipantsImport.Duplicates.Summary"), merges, Cases.Count - merges);
        }
    }

    public Task<IReadOnlyDictionary<int, Guid>?> Completion => _completion.Task;

    private void OnChoiceChanged() => OnPropertyChanged(nameof(Summary));

    [RelayCommand]
    private void MergeAll()
    {
        foreach (var item in Cases)
            item.SelectMerge();
    }

    [RelayCommand]
    private void AllNew()
    {
        foreach (var item in Cases)
            item.SelectNew();
    }

    [RelayCommand]
    private void Confirm()
    {
        var merges = Cases
            .Where(c => c.MergeTarget is not null)
            .ToDictionary(c => c.RowIndex, c => c.MergeTarget!.Value);
        _completion.TrySetResult(merges);
    }

    [RelayCommand]
    private void Cancel() => _completion.TrySetResult(null);
}

/// <summary>One clashing import row and its mutually exclusive choices.</summary>
public sealed partial class ImportDuplicateItem : ObservableObject
{
    private readonly ILocalizationService _localization;
    private readonly ImportDuplicateCase _case;
    private readonly Action _changed;

    public ImportDuplicateItem(ILocalizationService localization, ImportDuplicateCase duplicate, Action changed)
    {
        _localization = localization;
        _case = duplicate;
        _changed = changed;

        // Unique per card so the radios of different cards don't share one group.
        var groupName = "dup-" + duplicate.RowIndex;
        Choices = duplicate.Candidates
            .Select(c => new ImportDuplicateChoice(localization, groupName, duplicate.Imported, c, OnSelected))
            .Append(new ImportDuplicateChoice(localization, groupName, duplicate.Imported, null, OnSelected))
            .ToList();

        var preselected = Choices.FirstOrDefault(c => c.Candidate is { } cand && cand.LooksLikeSamePerson(duplicate.Imported))
                          ?? Choices[^1];
        preselected.IsSelected = true;
    }

    public int RowIndex => _case.RowIndex;

    public IReadOnlyList<ImportDuplicateChoice> Choices { get; }

    public string FullName => _case.Imported.FullName;

    public string Group => _case.Imported.Group;

    /// <summary>"У файлі: 2001 р.н. · Клуб · Регіон · ФСОУ 123".</summary>
    public string ImportedLine => _localization.Get("ParticipantsImport.Duplicates.InFile") + ": "
        + ImportDuplicateChoice.Details(
            _localization, _case.Imported.BirthYear, _case.Imported.Club, _case.Imported.Region, _case.Imported.FsouCode);

    /// <summary>The participant to merge into, or null for «новий учасник».</summary>
    public Guid? MergeTarget => Choices.FirstOrDefault(c => c.IsSelected)?.Candidate?.ParticipantId;

    public void SelectMerge()
    {
        // The best-looking candidate, falling back to the first one.
        var target = Choices.FirstOrDefault(c => c.Candidate is { } cand && cand.LooksLikeSamePerson(_case.Imported))
                     ?? Choices[0];
        target.IsSelected = true;
    }

    public void SelectNew() => Choices[^1].IsSelected = true;

    public void Refresh()
    {
        OnPropertyChanged(string.Empty);
        foreach (var choice in Choices)
            choice.Refresh();
    }

    // Keeps the choices exclusive when set from code (bulk buttons), not only through the radio group.
    private void OnSelected(ImportDuplicateChoice selected)
    {
        foreach (var choice in Choices)
        {
            if (!ReferenceEquals(choice, selected))
                choice.IsSelected = false;
        }
        OnPropertyChanged(nameof(MergeTarget));
        _changed();
    }
}

/// <summary>A radio in a duplicate card: merge into <see cref="Candidate"/>, or add as new when it is null.</summary>
public sealed partial class ImportDuplicateChoice : ObservableObject
{
    private readonly ILocalizationService _localization;
    private readonly ImportDuplicateRow _imported;
    private readonly Action<ImportDuplicateChoice> _selected;

    public ImportDuplicateChoice(
        ILocalizationService localization,
        string groupName,
        ImportDuplicateRow imported,
        ImportDuplicateCandidate? candidate,
        Action<ImportDuplicateChoice> selected)
    {
        _localization = localization;
        _imported = imported;
        _selected = selected;
        GroupName = groupName;
        Candidate = candidate;
    }

    public string GroupName { get; }

    public ImportDuplicateCandidate? Candidate { get; }

    public bool IsMerge => Candidate is not null;

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value)
    {
        if (value)
            _selected(this);
    }

    public string Label => Candidate is { } c
        ? string.Format(_localization.Get("ParticipantsImport.Duplicates.Merge"), c.FullName)
        : _localization.Get("ParticipantsImport.Duplicates.New");

    /// <summary>The candidate's details plus the days they ran in the group; blank for «новий».</summary>
    public string CandidateDetails
    {
        get
        {
            if (Candidate is not { } c)
                return string.Empty;
            var days = string.Format(_localization.Get("ParticipantsImport.Duplicates.Days"),
                _imported.Group, string.Join(", ", c.DayNumbers));
            var details = Details(_localization, c.BirthYear, c.Club, c.Region, c.FsouCode, allowEmpty: true);
            return details.Length == 0 ? days : details + " · " + days;
        }
    }

    /// <summary>True when the candidate's known birth year / FOU code contradict the file — likely a namesake.</summary>
    public bool IsMismatch => Candidate is { } c && !c.LooksLikeSamePerson(_imported);

    public string MismatchText => _localization.Get("ParticipantsImport.Duplicates.Mismatch");

    public void Refresh() => OnPropertyChanged(string.Empty);

    // "2001 р.н. · Клуб · Регіон · ФСОУ 123", skipping blanks.
    internal static string Details(
        ILocalizationService localization, int? birthYear, string club, string region, string fsouCode,
        bool allowEmpty = false)
    {
        var parts = new List<string>();
        if (birthYear is { } year)
            parts.Add(string.Format(localization.Get("ParticipantsImport.Duplicates.BirthYear"), year));
        if (club.Length > 0)
            parts.Add(club);
        if (region.Length > 0)
            parts.Add(region);
        if (fsouCode.Length > 0)
            parts.Add(string.Format(localization.Get("ParticipantsImport.Duplicates.Fsou"), fsouCode));
        if (parts.Count == 0)
            return allowEmpty ? string.Empty : localization.Get("ParticipantsImport.Duplicates.NoDetails");
        return string.Join(" · ", parts);
    }
}
