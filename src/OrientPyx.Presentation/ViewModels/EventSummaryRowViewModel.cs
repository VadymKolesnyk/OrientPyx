using CommunityToolkit.Mvvm.ComponentModel;
using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.Presentation.ViewModels;

/// <summary>
/// A single row in the competition selection table. Wraps an <see cref="EventSummary"/> so the shared
/// <c>SheetTable</c> can bind to it and the hidden flag can raise change notifications when toggled.
/// </summary>
public sealed partial class EventSummaryRowViewModel : ObservableObject
{
    public EventSummaryRowViewModel(EventSummary summary)
    {
        Summary = summary;
    }

    public EventSummary Summary { get; }

    public string Identifier => Summary.Identifier;
    public string Name => Summary.Name;
    public string Venue => Summary.Venue;
    public string DateRange => Summary.DateRange;
    public int DayCount => Summary.DayCount;

    /// <summary>Mirrors <see cref="EventSummary.IsHidden"/>; raise it when the flag is toggled.</summary>
    public bool IsHidden => Summary.IsHidden;

    /// <summary>Call after <see cref="EventSummary.IsHidden"/> has changed to refresh the row.</summary>
    public void RaiseHiddenChanged() => OnPropertyChanged(nameof(IsHidden));
}
