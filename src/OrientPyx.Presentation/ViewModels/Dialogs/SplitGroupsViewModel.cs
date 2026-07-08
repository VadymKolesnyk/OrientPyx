using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Localization;

namespace OrientPyx.Presentation.ViewModels.Dialogs;

/// <summary>
/// Preprocessing modal for the IOF XML import: shows each parsed course with its name already
/// split into the constituent groups (e.g. "ЧЖ55" → "Ч55", "Ж55"), and lets the user edit names,
/// add groups, or remove them before importing. Confirming rebuilds the <see cref="IofCourseData"/>
/// so each non-blank group becomes its own course (cloning the original course's controls/length/
/// climb); the controls list and version/scale are carried through unchanged.
///
/// Callers <c>await</c> <see cref="Completion"/> for the rewritten data, or null on cancel. Mirrors
/// the <see cref="ImportOptionsViewModel"/> TaskCompletionSource pattern.
/// </summary>
public sealed partial class SplitGroupsViewModel : ObservableObject
{
    private readonly TaskCompletionSource<IofCourseData?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly IofCourseData _source;

    public SplitGroupsViewModel(
        ILocalizationService localization,
        ICourseNameSplitter splitter,
        IofCourseData data)
    {
        Localization = localization;
        _source = data;

        Courses = new ObservableCollection<SplitCourseRow>(
            data.Courses.Select(course => new SplitCourseRow(localization, course, splitter.Split(course.Name))));

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

                courses.Add(new IofCourse
                {
                    Name = name,
                    Length = row.Source.Length,
                    Climb = row.Source.Climb,
                    ControlCodes = row.Source.ControlCodes,
                    // Carry the scatter variants through so a split group keeps its розсіювання orders; each
                    // group produced from a scatter course inherits the same variant set.
                    Variants = row.Source.Variants,
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

/// <summary>One parsed course inside the splitter, with its editable list of split group names.</summary>
public sealed partial class SplitCourseRow : ObservableObject
{
    private readonly ILocalizationService _localization;

    public SplitCourseRow(ILocalizationService localization, IofCourse source, IReadOnlyList<string> groups)
    {
        _localization = localization;
        Source = source;
        Groups = new ObservableCollection<SplitGroupEntry>(groups.Select(g => new SplitGroupEntry(g)));
        AddGroupLabel = localization.Get("Split.AddGroup");
    }

    /// <summary>The original parsed course this row was built from.</summary>
    public IofCourse Source { get; }

    /// <summary>Original course name as written in the file (shown as the row heading).</summary>
    public string OriginalName => Source.Name;

    /// <summary>True when this course is a scatter («розсіювання») course — it carries more than one variant.</summary>
    public bool HasVariants => Source.Variants.Count > 1;

    /// <summary>Localized note that the course imports as N розсіювань (only shown when <see cref="HasVariants"/>).</summary>
    public string VariantsLabel => HasVariants
        ? string.Format(_localization.Get("Split.Scatter"), Source.Variants.Count)
        : string.Empty;

    /// <summary>The split group names; edited, added to, and removed from in the dialog.</summary>
    public ObservableCollection<SplitGroupEntry> Groups { get; }

    /// <summary>Localized caption for the add-group button; refreshed on language change.</summary>
    [ObservableProperty]
    private string _addGroupLabel = string.Empty;

    public void RefreshLabel()
    {
        AddGroupLabel = _localization.Get("Split.AddGroup");
        OnPropertyChanged(nameof(VariantsLabel));
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
