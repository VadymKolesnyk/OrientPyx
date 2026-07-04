using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.Localization;

namespace OrientPyx.Presentation.ViewModels.Dialogs;

/// <summary>
/// Explains, for one group chip on the draw page, why it is highlighted red — which other groups it
/// overlaps with (same start minutes in a different lane) and what they share (the opening control point,
/// or the whole course). Unlike the static per-screen help, this dialog's content is <b>built dynamically</b>
/// by <see cref="Pages.DrawViewModel"/> from the current arrangement, so the header/intro come pre-resolved
/// as plain strings rather than localization keys.
/// </summary>
public sealed partial class DrawClashHelpViewModel : ObservableObject
{
    private readonly TaskCompletionSource<bool> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <param name="title">Pre-resolved dialog title (e.g. "Чому «Ч21» підсвічено").</param>
    /// <param name="intro">One-line summary of the clash type shown under the title.</param>
    /// <param name="courseLabel">This group's own course order line ("Порядок КП: 31 → 42 → …").</param>
    /// <param name="clashes">One entry per overlapping group; each has a heading + detail lines.</param>
    public DrawClashHelpViewModel(
        ILocalizationService localization,
        string title,
        string intro,
        string courseLabel,
        IReadOnlyList<DrawClashEntry> clashes)
    {
        Localization = localization;
        Title = title;
        Intro = intro;
        CourseLabel = courseLabel;
        Clashes = new ObservableCollection<DrawClashEntry>(clashes);
    }

    public ILocalizationService Localization { get; }

    public string Title { get; }
    public string Intro { get; }
    public string CourseLabel { get; }
    public ObservableCollection<DrawClashEntry> Clashes { get; }

    /// <summary>Completes when the user closes the dialog (the result is unused).</summary>
    public Task<bool> Completion => _completion.Task;

    [RelayCommand]
    private void Close() => _completion.TrySetResult(true);
}

/// <summary>
/// One overlapping group in the clash explanation: a heading (the other group + its lane and the shared
/// thing) and detail lines (the overlapping start times, its course order).
/// </summary>
public sealed record DrawClashEntry(string Heading, IReadOnlyList<string> Details);
