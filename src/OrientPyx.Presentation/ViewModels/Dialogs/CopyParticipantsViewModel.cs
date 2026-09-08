using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Localization;
using OrientPyx.Presentation.ViewModels.Pages;

namespace OrientPyx.Presentation.ViewModels.Dialogs;

/// <summary>
/// Modal for «Копіювання учасників з дня в день»: pick the source and target day, then tick which per-day
/// fields travel. The group is not a checkbox — it always travels (a member needs a group) — which the
/// dialog states as a fixed line instead. «Оплата» is offered only in per-day payment mode, since outside
/// it the payment is competition-level and copying a day would mean nothing. Callers <c>await</c>
/// <see cref="Completion"/> for the chosen days + options, or null on cancel. Mirrors the
/// <see cref="AssignChipsViewModel"/> pattern.
/// </summary>
public sealed partial class CopyParticipantsViewModel : ObservableObject
{
    private readonly TaskCompletionSource<CopyParticipantsRequest?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public CopyParticipantsViewModel(
        ILocalizationService localization,
        IReadOnlyList<DayOption> days,
        bool paymentPerDay,
        DayOption? initialSource = null)
    {
        Localization = localization;
        Days = new ObservableCollection<DayOption>(days);
        ShowPayment = paymentPerDay;

        // Open on the day in view as the source, and on a different day as the target so the dialog is
        // valid the moment it appears.
        _selectedSource = initialSource is not null && Days.Contains(initialSource)
            ? initialSource
            : Days.FirstOrDefault();
        _selectedTarget = Days.FirstOrDefault(d => !ReferenceEquals(d, SelectedSource));

        Localization.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(GroupNote));
        };
    }

    public ILocalizationService Localization { get; }

    public string Title => Localization.Get("Participants.CopyDay.Title");

    /// <summary>The always-on group rule, shown as a note where the other fields have checkboxes.</summary>
    public string GroupNote => Localization.Get("Participants.CopyDay.GroupNote");

    /// <summary>The days to choose between (real days only — the roster is not a copy source).</summary>
    public ObservableCollection<DayOption> Days { get; }

    /// <summary>True only in per-day payment mode, where copying «Оплата» is meaningful.</summary>
    public bool ShowPayment { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    private DayOption? _selectedSource;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    private DayOption? _selectedTarget;

    /// <summary>Chip: on by default — the same person usually keeps their chip across days.</summary>
    [ObservableProperty]
    private bool _copyChip = true;

    /// <summary>Payment (note + raised fee): off by default — each day is normally paid for separately.</summary>
    [ObservableProperty]
    private bool _copyPayment;

    /// <summary>Start minute: off by default — the next day is normally drawn afresh.</summary>
    [ObservableProperty]
    private bool _copyStartTime;

    /// <summary>«Поза конкурсом»: on by default — it is a property of the runner, not of one day.</summary>
    [ObservableProperty]
    private bool _copyOutOfCompetition = true;

    /// <summary>Both days picked and different from each other.</summary>
    public bool CanConfirm =>
        SelectedSource is not null
        && SelectedTarget is not null
        && !ReferenceEquals(SelectedSource, SelectedTarget);

    /// <summary>Completes with the chosen days + options on confirm, or null on cancel/close.</summary>
    public Task<CopyParticipantsRequest?> Completion => _completion.Task;

    [RelayCommand]
    private void Confirm()
    {
        if (!CanConfirm || SelectedSource?.Day is not { } source || SelectedTarget?.Day is not { } target)
        {
            _completion.TrySetResult(null);
            return;
        }

        _completion.TrySetResult(new CopyParticipantsRequest(
            source.Id,
            target.Id,
            new CopyParticipantsOptions(
                Chip: CopyChip,
                // Outside per-day payment mode the checkbox is hidden, so never copy on its stale value.
                Payment: ShowPayment && CopyPayment,
                StartTime: CopyStartTime,
                OutOfCompetition: CopyOutOfCompetition)));
    }

    [RelayCommand]
    private void Cancel() => _completion.TrySetResult(null);
}
