using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.Localization;

namespace OrientPyx.Presentation.ViewModels.Dialogs;

/// <summary>
/// Modal for bulk-assigning rental chips to the participants currently shown in the table: a dropdown
/// picks which chips to draw from — all of them, or only those carrying a given note (chip "type"). The
/// action then hands out the unused chips of that selection, by ascending number, to every shown
/// participant (or member day) that has no chip yet. Callers <c>await</c> <see cref="Completion"/> for
/// the chosen note filter, or null on cancel. Mirrors the <see cref="BulkAddChipsViewModel"/> pattern.
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

    /// <summary>The note filter choices, in display order.</summary>
    public ObservableCollection<ChipNoteOption> Notes { get; }

    /// <summary>The currently selected note filter (the "all" sentinel by default).</summary>
    [ObservableProperty]
    private ChipNoteOption _selectedNote;

    /// <summary>Completes with the chosen filter on confirm, or null on cancel/close.</summary>
    public Task<AssignChipsResult?> Completion => _completion.Task;

    [RelayCommand]
    private void Confirm() =>
        _completion.TrySetResult(new AssignChipsResult(SelectedNote.IsAll ? null : SelectedNote.Note));

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

/// <summary>The chosen note filter. <see cref="Note"/> is null for "all chips", otherwise the exact note to match.</summary>
public sealed record AssignChipsResult(string? Note);
