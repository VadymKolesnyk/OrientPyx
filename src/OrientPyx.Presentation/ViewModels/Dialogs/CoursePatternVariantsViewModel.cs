using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.BusinessLogic.Disciplines.CoursePattern;
using OrientPyx.Localization;

namespace OrientPyx.Presentation.ViewModels.Dialogs;

/// <summary>
/// «Всі варіанти»: lists every concrete passage order the group's mixed-discipline pattern allows — each
/// free-choice block expanded to every way of picking its N controls and every order they can be run in.
/// A pattern with no <c>[N: …]</c> block has exactly one. Wide blocks grow factorially, so the list is
/// capped (<see cref="CoursePattern.VariantLimit"/>) and a note says how many were left out.
/// </summary>
public sealed partial class CoursePatternVariantsViewModel : ObservableObject
{
    private readonly TaskCompletionSource<bool> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public CoursePatternVariantsViewModel(ILocalizationService localization, string? patternText,
        string? startCode = null, string? finishCode = null)
    {
        Localization = localization;

        var pattern = CoursePattern.Parse(patternText, startCode, finishCode);
        PatternText = pattern.NormalizedOrder();
        IsPatternValid = pattern.IsValid;

        var set = pattern.EnumerateOrders();
        for (var i = 0; i < set.Variants.Count; i++)
            Variants.Add(new CourseVariantViewModel(i + 1, set.Variants[i].Text));

        HasVariants = Variants.Count > 0;
        Summary = string.Format(localization.Get("CoursePattern.Variants.Summary"), set.TotalCount);
        IsTruncated = set.Truncated;
        TruncatedNote = set.Truncated
            ? string.Format(
                localization.Get("CoursePattern.Variants.Truncated"),
                Variants.Count, set.TotalCount)
            : string.Empty;
    }

    public ILocalizationService Localization { get; }

    /// <summary>Completes when the user closes the dialog (the result is unused).</summary>
    public Task<bool> Completion => _completion.Task;

    /// <summary>The group's pattern, normalized to "S … F" — shown read-only above the list.</summary>
    public string PatternText { get; }

    /// <summary>False when the pattern has a structural error, so the listing can't be trusted.</summary>
    public bool IsPatternValid { get; }

    /// <summary>«Усього варіантів: N» — the true count, even when the list below is capped.</summary>
    public string Summary { get; }

    /// <summary>True when the expansion hit the cap, so only the first ones are listed.</summary>
    public bool IsTruncated { get; }

    /// <summary>The "showing X of N" note; empty when nothing was left out.</summary>
    public string TruncatedNote { get; }

    /// <summary>True when at least one order was produced (an empty pattern produces none worth showing).</summary>
    public bool HasVariants { get; }

    /// <summary>The listed passage orders, numbered from 1.</summary>
    public ObservableCollection<CourseVariantViewModel> Variants { get; } = [];

    /// <summary>Every listed order as one pastable block, one per line — what the «копіювати» button puts
    /// on the clipboard (the copy itself happens in the view's code-behind, per the app's convention).</summary>
    public string ClipboardText => string.Join(Environment.NewLine, Variants.Select(x => x.Order));

    [RelayCommand]
    private void Close() => _completion.TrySetResult(true);
}

/// <summary>One listed passage order: its position in the list and the codes as a single line.</summary>
/// <param name="Index">1-based position, shown as the row's number.</param>
/// <param name="Order">The control codes in punching order, space-separated.</param>
public sealed record CourseVariantViewModel(int Index, string Order);
