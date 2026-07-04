using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.Localization;

namespace OrientPyx.Presentation.ViewModels.Dialogs;

/// <summary>
/// Modal for marking the current day's «проблемні КП» — control points that stopped working during the
/// competition. The user ticks each broken control; on save it is dropped from the prescribed/allowed
/// course everywhere it is required, so a runner who missed it is not penalised (no MP / not counted) and
/// the splits show it flagged «вимкнено». Callers <c>await</c> <see cref="Completion"/> for the ids of the
/// controls to disable, or null on cancel. Opened from the read-out (зчитка) page.
/// </summary>
public sealed partial class ProblematicControlsViewModel : ObservableObject
{
    private readonly TaskCompletionSource<IReadOnlyList<Guid>?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ProblematicControlsViewModel(
        ILocalizationService localization, IReadOnlyList<ProblematicControlItem> controls)
    {
        Localization = localization;
        Controls = new ObservableCollection<ProblematicControlItem>(controls);
        Localization.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Hint));
            OnPropertyChanged(nameof(Empty));
        };
    }

    public ILocalizationService Localization { get; }

    public string Title => Localization.Get("FinishRead.Problematic.Title");

    public string Hint => Localization.Get("FinishRead.Problematic.Hint");

    /// <summary>Shown instead of the list when the day has no control points to choose from.</summary>
    public string Empty => Localization.Get("FinishRead.Problematic.Empty");

    /// <summary>The day's control points, each a toggleable row; <see cref="ProblematicControlItem.IsDisabled"/> two-way bound.</summary>
    public ObservableCollection<ProblematicControlItem> Controls { get; }

    public bool HasControls => Controls.Count > 0;

    /// <summary>Completes with the ids of the controls to disable on confirm, or null on cancel/close.</summary>
    public Task<IReadOnlyList<Guid>?> Completion => _completion.Task;

    [RelayCommand]
    private void Confirm() =>
        _completion.TrySetResult(Controls.Where(c => c.IsDisabled).Select(c => c.Id).ToList());

    [RelayCommand]
    private void Cancel() => _completion.TrySetResult(null);
}

/// <summary>
/// One control-point row in the «проблемні КП» modal: its id, the display label (code + an optional type
/// hint), and whether it is currently marked disabled (two-way bound to the checkbox).
/// </summary>
public sealed partial class ProblematicControlItem : ObservableObject
{
    public ProblematicControlItem(Guid id, string label, bool isDisabled)
    {
        Id = id;
        Label = label;
        _isDisabled = isDisabled;
    }

    public Guid Id { get; }

    public string Label { get; }

    [ObservableProperty]
    private bool _isDisabled;
}
