using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Localization;

namespace OrientPyx.Presentation.ViewModels.Dialogs;

/// <summary>
/// Preprocessing modal for the IOF XML import: shows each parsed course with its name already
/// split into the constituent groups (e.g. "ЧЖ55" → "Ч55", "Ж55"), and lets the user edit names,
/// add groups, or remove them before importing. A course with several variations also asks how to use them —
/// «Розсіювання» or «Естафета», and for a relay whether a leg number runs its own variation («варіант за номером
/// етапу») or the team shares its variations out over its legs («набір на команду»). Confirming rebuilds the
/// <see cref="IofCourseData"/> so each non-blank group becomes its own course (cloning the original course's
/// controls/length/climb/variations, with the chosen use); the controls list and version/scale are carried through.
///
/// Callers <c>await</c> <see cref="Completion"/> for the rewritten data, or null on cancel. Mirrors
/// the <see cref="ImportOptionsViewModel"/> TaskCompletionSource pattern.
/// </summary>
public sealed partial class SplitGroupsViewModel : ObservableObject
{
    private readonly TaskCompletionSource<IofCourseData?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly IofCourseData _source;

    /// <param name="dayIsRelay">The day's own type is a relay — a course with variations then defaults to «Естафета».</param>
    /// <param name="relayGroups">The day's relay groups by name, with their leg-course mode — a course splitting into one
    /// of them defaults to «Естафета» in that mode.</param>
    public SplitGroupsViewModel(
        ILocalizationService localization,
        ICourseNameSplitter splitter,
        IofCourseData data,
        bool dayIsRelay = false,
        IReadOnlyDictionary<string, RelayCourseMode>? relayGroups = null)
    {
        Localization = localization;
        _source = data;

        Courses = new ObservableCollection<SplitCourseRow>(
            data.Courses.Select(course => new SplitCourseRow(
                localization, course, splitter.Split(course.Name), dayIsRelay, relayGroups)));

        Localization.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Message));
            foreach (var row in Courses)
                row.RefreshLabel();
        };
    }

    public ILocalizationService Localization { get; }

    public string Title => Localization.Get("Split.Title");

    public string Message => Localization.Get("Split.Message");

    /// <summary>One row per parsed course, each with its (editable) split groups.</summary>
    public ObservableCollection<SplitCourseRow> Courses { get; }

    /// <summary>Completes with the rewritten course data on confirm, or null on cancel/close.</summary>
    public Task<IofCourseData?> Completion => _completion.Task;

    [RelayCommand]
    private void Confirm()
    {
        var courses = new List<IofCourse>();
        foreach (var row in Courses)
        {
            foreach (var entry in row.Groups)
            {
                var name = (entry.Name ?? string.Empty).Trim();
                if (name.Length == 0)
                    continue;

                // Every group split from a course inherits its variations, the file's number assignments and the use
                // chosen for them.
                courses.Add(new IofCourse
                {
                    Name = name,
                    Length = row.Source.Length,
                    Climb = row.Source.Climb,
                    ControlCodes = row.Source.ControlCodes,
                    Variants = row.Source.Variants,
                    AllVariants = row.Source.AllVariants,
                    TeamCombinations = row.Source.TeamCombinations,
                    VariationUse = row.VariationUse,
                });
            }
        }

        _completion.TrySetResult(new IofCourseData
        {
            Version = _source.Version,
            MapScale = _source.MapScale,
            Controls = _source.Controls,
            Courses = courses,
        });
    }

    [RelayCommand]
    private void Cancel() => _completion.TrySetResult(null);
}

/// <summary>One parsed course inside the splitter, with its editable list of split group names and — for a course
/// with variations — the choice of how to import them.</summary>
public sealed partial class SplitCourseRow : ObservableObject
{
    private readonly ILocalizationService _localization;

    public SplitCourseRow(
        ILocalizationService localization, IofCourse source, IReadOnlyList<string> groups,
        bool dayIsRelay = false, IReadOnlyDictionary<string, RelayCourseMode>? relayGroups = null)
    {
        _localization = localization;
        Source = source;
        Groups = new ObservableCollection<SplitGroupEntry>(groups.Select(g => new SplitGroupEntry(g)));
        AddGroupLabel = localization.Get("Split.AddGroup");

        // The default use: a group already running a relay keeps its mode; an OCAD relay (team combinations over
        // several legs) or a relay day makes a relay by leg number; anything else is a scatter.
        if (HasVariations)
        {
            var existing = groups
                .Select(g => relayGroups is not null && relayGroups.TryGetValue(g.Trim(), out var mode) ? mode : (RelayCourseMode?)null)
                .FirstOrDefault(m => m is not null);
            if (existing is { } mode)
            {
                _isRelay = true;
                _isTeamSet = mode == RelayCourseMode.TeamSet;
            }
            else
                _isRelay = source.CombinationLegCount > 1 || dayIsRelay;
        }
    }

    public ILocalizationService Localization => _localization;

    /// <summary>The original parsed course this row was built from.</summary>
    public IofCourse Source { get; }

    /// <summary>Original course name as written in the file (shown as the row heading).</summary>
    public string OriginalName => Source.Name;

    /// <summary>True when the file gives this course several variations — the row then asks how to use them.</summary>
    public bool HasVariations => Source.HasVariations;

    /// <summary>«Естафета» chosen (else «Розсіювання»).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsScatter), nameof(VariationUse), nameof(VariantsLabel))]
    private bool _isRelay;

    /// <summary>«Розсіювання» chosen — the radio twin of <see cref="IsRelay"/>.</summary>
    public bool IsScatter
    {
        get => !IsRelay;
        set => IsRelay = !value;
    }

    /// <summary>Relay «набір на команду» chosen (else «варіант за номером етапу»).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsByBib), nameof(VariationUse))]
    private bool _isTeamSet;

    /// <summary>Relay «варіант за номером етапу» chosen — the radio twin of <see cref="IsTeamSet"/>.</summary>
    public bool IsByBib
    {
        get => !IsTeamSet;
        set => IsTeamSet = !value;
    }

    /// <summary>The use handed to the import.</summary>
    public CourseVariationUse VariationUse =>
        !HasVariations || !IsRelay ? CourseVariationUse.Scatter
        : IsTeamSet ? CourseVariationUse.RelayTeamSet
        : CourseVariationUse.RelayByBib;

    /// <summary>What the import will make of the variations (only shown when <see cref="HasVariations"/>).</summary>
    public string VariantsLabel
    {
        get
        {
            if (!HasVariations)
                return string.Empty;
            if (IsRelay)
                return string.Format(_localization.Get("Split.Relay"), Source.AllVariants.Count);
            if (Source.Variants.Count <= 1)
                return _localization.Get("Split.ScatterSingle");
            var count = Source.TeamCombinations.Count > 0 ? Source.AllVariants.Count : Source.Variants.Count;
            return string.Format(_localization.Get("Split.Scatter"), count);
        }
    }

    /// <summary>Whether (and for how many numbers) the file hands the variations out.</summary>
    public string CombinationsLabel => Source.TeamCombinations.Count > 0
        ? string.Format(_localization.Get("Split.Combinations"), Source.TeamCombinations.Count, Source.CombinationLegCount)
        : _localization.Get("Split.NoCombinations");

    /// <summary>The split group names; edited, added to, and removed from in the dialog.</summary>
    public ObservableCollection<SplitGroupEntry> Groups { get; }

    /// <summary>Localized caption for the add-group button; refreshed on language change.</summary>
    [ObservableProperty]
    private string _addGroupLabel = string.Empty;

    public void RefreshLabel()
    {
        AddGroupLabel = _localization.Get("Split.AddGroup");
        OnPropertyChanged(nameof(VariantsLabel));
        OnPropertyChanged(nameof(CombinationsLabel));
    }

    [RelayCommand]
    private void AddGroup() => Groups.Add(new SplitGroupEntry(string.Empty));

    [RelayCommand]
    private void RemoveGroup(SplitGroupEntry entry) => Groups.Remove(entry);
}

/// <summary>One editable group name inside a <see cref="SplitCourseRow"/>.</summary>
public sealed partial class SplitGroupEntry : ObservableObject
{
    public SplitGroupEntry(string name) => _name = name;

    [ObservableProperty]
    private string _name;
}
