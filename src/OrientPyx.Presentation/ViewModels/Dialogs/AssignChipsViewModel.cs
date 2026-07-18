using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.Localization;

namespace OrientPyx.Presentation.ViewModels.Dialogs;

/// <summary>
/// Modal for bulk-assigning rental chips to the participants currently shown in the table. It offers two
/// modes, chosen at the top of the dialog:
/// <list type="bullet">
/// <item><b>From the rental base</b> — a dropdown narrows the existing pool to all chips or only those
/// carrying a given note ("type"); the action hands out those unused chips by ascending number.</item>
/// <item><b>Starting from a number</b> — a start number + count first add that contiguous range to the
/// rental base (skipping any that already exist), then those very chips are handed out. One modal does
/// both steps.</item>
/// </list>
/// Callers <c>await</c> <see cref="Completion"/> for the chosen mode/parameters, or null on cancel.
/// Mirrors the <see cref="BulkAddChipsViewModel"/> pattern.
/// </summary>
public sealed partial class AssignChipsViewModel : ObservableObject
{
    private readonly TaskCompletionSource<AssignChipsResult?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <param name="noteOptions">The note filter choices: a leading "all" sentinel, then each distinct note.</param>
    public AssignChipsViewModel(ILocalizationService localization, IReadOnlyList<ChipNoteOption> noteOptions)
    {
        Localization = localization;
        Notes = new ObservableCollection<ChipNoteOption>(noteOptions);
        _selectedNote = Notes[0];
        Localization.PropertyChanged += (_, _) => OnPropertyChanged(nameof(Title));
    }

    public ILocalizationService Localization { get; }

    public string Title => Localization.Get("Participants.AssignChips.Title");

    /// <summary>True when the "from the rental base" mode is chosen (the default).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRangeMode))]
    private bool _fromBase = true;

    // IsRangeMode is derived from FromBase; the generated OnFromBaseChanged partial isn't emitted for a
    // NotifyPropertyChangedFor target, so the attribute above already re-raises IsRangeMode on every change.

    /// <summary>True when the "starting from a number" mode is chosen — the two-way mirror of
    /// <see cref="FromBase"/>, so both mode radio buttons write back to the same underlying flag.</summary>
    public bool IsRangeMode
    {
        get => !FromBase;
        set => FromBase = !value;
    }

    /// <summary>The note filter choices, in display order (used by the "from the rental base" mode).</summary>
    public ObservableCollection<ChipNoteOption> Notes { get; }

    /// <summary>The currently selected note filter (the "all" sentinel by default).</summary>
    [ObservableProperty]
    private ChipNoteOption _selectedNote;

    /// <summary>First chip number of the range to add-and-assign (the "starting from a number" mode).
    /// Leading zeros are preserved. E.g. "9007400".</summary>
    [ObservableProperty]
    private string _startNumber = string.Empty;

    /// <summary>How many chips to add-and-assign in range mode. Typed into a digit-only text box.</summary>
    [ObservableProperty]
    private int _count = 100;

    /// <summary>Optional note applied to every chip added in range mode.</summary>
    [ObservableProperty]
    private string _rangeNote = string.Empty;

    /// <summary>Completes with the chosen mode/parameters on confirm, or null on cancel/close.</summary>
    public Task<AssignChipsResult?> Completion => _completion.Task;

    [RelayCommand]
    private void Confirm()
    {
        if (IsRangeMode)
        {
            _completion.TrySetResult(AssignChipsResult.Range(
                (StartNumber ?? string.Empty).Trim(),
                Count,
                (RangeNote ?? string.Empty).Trim()));
            return;
        }

        _completion.TrySetResult(AssignChipsResult.FromBase(SelectedNote.IsAll ? null : SelectedNote.Note));
    }

    [RelayCommand]
    private void Cancel() => _completion.TrySetResult(null);
}

/// <summary>One choice in the assign-chips note dropdown: the "all" sentinel, or a specific note.</summary>
public sealed class ChipNoteOption
{
    private ChipNoteOption(bool isAll, string note, string label)
    {
        IsAll = isAll;
        Note = note;
        Label = label;
    }

    /// <summary>True for the leading "all chips" sentinel (no note filter).</summary>
    public bool IsAll { get; }

    /// <summary>The note this option filters by (empty for the "(no note)" option). Unused when <see cref="IsAll"/>.</summary>
    public string Note { get; }

    /// <summary>The text shown in the dropdown.</summary>
    public string Label { get; }

    public static ChipNoteOption All(string label) => new(isAll: true, note: string.Empty, label);

    public static ChipNoteOption ForNote(string note, string label) => new(isAll: false, note, label);
}

/// <summary>
/// The chosen assign-chips action. Either draw from the existing rental base filtered by a note
/// (<see cref="Note"/> null = all chips), or first add a contiguous range (<see cref="StartNumber"/> /
/// <see cref="Count"/> / <see cref="RangeNote"/>) then assign it. <see cref="IsRange"/> selects which.
/// </summary>
public sealed record AssignChipsResult(bool IsRange, string? Note, string StartNumber, int Count, string RangeNote)
{
    /// <summary>Draw from the existing rental base; <paramref name="note"/> null = all chips.</summary>
    public static AssignChipsResult FromBase(string? note) =>
        new(IsRange: false, note, StartNumber: string.Empty, Count: 0, RangeNote: string.Empty);

    /// <summary>Add the range first, then assign it.</summary>
    public static AssignChipsResult Range(string startNumber, int count, string rangeNote) =>
        new(IsRange: true, Note: null, startNumber, count, rangeNote);
}
