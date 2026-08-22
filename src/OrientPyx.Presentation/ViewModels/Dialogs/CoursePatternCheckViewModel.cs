using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.BusinessLogic.Disciplines.CoursePattern;
using OrientPyx.Localization;

namespace OrientPyx.Presentation.ViewModels.Dialogs;

/// <summary>
/// «Перевірити порядок»: a scratch pad for the mixed-discipline order pattern. The operator types a
/// passage (the КП codes a runner would punch, in order) and the modal says whether the group's pattern
/// accepts it — using the very same <see cref="CoursePattern.Match"/> walk the read-out uses, so what it
/// reports is exactly how such a run would be judged. Each typed code is echoed back marked as on-course
/// or foreign, and a failing passage names the first control the pattern could not satisfy.
/// </summary>
public sealed partial class CoursePatternCheckViewModel : ObservableObject
{
    private readonly TaskCompletionSource<bool> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly CoursePattern _pattern;

    public CoursePatternCheckViewModel(ILocalizationService localization, string? patternText,
        string? startCode = null, string? finishCode = null)
    {
        Localization = localization;
        _pattern = CoursePattern.Parse(patternText, startCode, finishCode);
        PatternText = _pattern.NormalizedOrder();
        Evaluate();
    }

    public ILocalizationService Localization { get; }

    /// <summary>Completes when the user closes the dialog (the result is unused).</summary>
    public Task<bool> Completion => _completion.Task;

    /// <summary>The group's pattern, normalized to "S … F" — shown read-only above the input.</summary>
    public string PatternText { get; }

    /// <summary>The order being tested, as typed (codes separated by spaces, commas or dashes).</summary>
    [ObservableProperty]
    private string _order = string.Empty;

    /// <summary>True once the operator has typed something, so a verdict is worth showing.</summary>
    [ObservableProperty]
    private bool _hasVerdict;

    /// <summary>True when the typed order satisfies the pattern (drives the ✓/✕ glyph and colour).</summary>
    [ObservableProperty]
    private bool _isOrderValid;

    /// <summary>The verdict line: "порядок правильний", or which control is missing.</summary>
    [ObservableProperty]
    private string _verdict = string.Empty;

    /// <summary>The typed codes, each flagged on-course or foreign — the passage read back.</summary>
    public ObservableCollection<CheckedPunchViewModel> Punches { get; } = [];

    partial void OnOrderChanged(string value) => Evaluate();

    // Re-runs the check for the current input. An empty input clears the verdict rather than reporting a
    // failure — nothing has been asked yet.
    private void Evaluate()
    {
        Punches.Clear();

        var codes = CoursePattern.SplitOrder(Order);
        if (codes.Count == 0)
        {
            HasVerdict = false;
            IsOrderValid = false;
            Verdict = string.Empty;
            return;
        }

        var ok = _pattern.CheckOrder(codes, out var firstMissing, out var onCourse);
        var onCourseHint = Localization.Get("CoursePattern.Check.Punch.OnCourse");
        var extraHint = Localization.Get("CoursePattern.Check.Punch.Extra");
        for (var i = 0; i < codes.Count; i++)
            Punches.Add(new CheckedPunchViewModel(
                codes[i], onCourse[i], onCourse[i] ? onCourseHint : extraHint));

        HasVerdict = true;
        IsOrderValid = ok;
        Verdict = ok
            ? Localization.Get("CoursePattern.Check.Valid")
            : string.Format(
                Localization.Get("CoursePattern.Check.Invalid"),
                string.IsNullOrWhiteSpace(firstMissing) ? "—" : firstMissing);
    }

    [RelayCommand]
    private void Clear() => Order = string.Empty;

    [RelayCommand]
    private void Close() => _completion.TrySetResult(true);
}

/// <summary>One typed code of the tested passage: its number and whether the pattern consumed it (on-course)
/// or skipped it as a foreign/extra punch. An extra is dimmed and struck through, so the chips read at a
/// glance as "these counted, these were ignored".</summary>
/// <param name="Code">The КП code as typed.</param>
/// <param name="OnCourse">True when the pattern used this punch; false when it is an extra.</param>
/// <param name="Hint">The tooltip explaining which of the two this chip is.</param>
public sealed record CheckedPunchViewModel(string Code, bool OnCourse, string Hint)
{
    /// <summary>Full strength for an on-course punch, dimmed for an ignored extra.</summary>
    public double Opacity => OnCourse ? 1.0 : 0.45;

    /// <summary>An extra punch is struck through; an on-course one is left plain.</summary>
    public TextDecorationCollection? Decorations => OnCourse ? null : TextDecorations.Strikethrough;
}
