using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.Localization;

namespace OrientPyx.Presentation.ViewModels.Dialogs;

/// <summary>
/// A read-only help modal that explains how to write the «mixed» discipline's course-order pattern —
/// the ordered <c>&lt;…&gt;</c> sequence, the <c>[N …]</c> any-of block and nesting — with worked
/// examples. Opened from the course-order editor's «?» button; closed with a single button.
/// All text resolves through the localization indexer in XAML so it re-localizes on a language switch.
/// </summary>
public sealed partial class CoursePatternHelpViewModel : ObservableObject
{
    private readonly TaskCompletionSource<bool> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public CoursePatternHelpViewModel(ILocalizationService localization)
    {
        Localization = localization;
    }

    public ILocalizationService Localization { get; }

    /// <summary>Completes when the user closes the dialog (the result is unused).</summary>
    public Task<bool> Completion => _completion.Task;

    [RelayCommand]
    private void Close() => _completion.TrySetResult(true);
}
