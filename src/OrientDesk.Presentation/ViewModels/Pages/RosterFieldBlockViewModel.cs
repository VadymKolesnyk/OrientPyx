using CommunityToolkit.Mvvm.ComponentModel;

namespace OrientDesk.Presentation.ViewModels.Pages;

/// <summary>The per-day fields the roster ("Мандатка") groups into collapsible blocks.</summary>
public enum RosterField
{
    Groups,
    Chips,
    StartTimes,
    OutOfCompetition,

    // Computed run-result fields (read-only except ResultStatus). These blocks always render one column
    // per day (the collapse toggle is hidden for them — a merged read-only summary isn't meaningful).
    ActualStart,
    Finish,
    ResultStatus,
    Result,
    Place,
    Score
}

/// <summary>
/// One collapsible block of per-day columns in the roster ("Мандатка") grid — a single field
/// (group, chip, …) shown across every day. Collapsed (the default) it renders one merged cell per
/// row; expanded it renders one column per day. The block's "relevant days" rule decides which day
/// cells the merged view considers and writes to: <b>Groups</b> spans all days, while <b>Chips</b>
/// (and future per-member fields) span only the days the participant runs.
///
/// State is held in-memory by the page (a singleton VM) and is not persisted.
/// </summary>
public sealed partial class RosterFieldBlockViewModel : ObservableObject
{
    private readonly Func<RosterDayCellViewModel, bool> _isRelevant;

    public RosterFieldBlockViewModel(
        RosterField field,
        string labelKey,
        Func<RosterDayCellViewModel, bool> isRelevant)
    {
        Field = field;
        LabelKey = labelKey;
        _isRelevant = isRelevant;
    }

    /// <summary>Which per-day field this block shows.</summary>
    public RosterField Field { get; }

    /// <summary>Localization key for the block's name on its toggle button ("Групи" / "Чіпи").</summary>
    public string LabelKey { get; }

    /// <summary>True while the block shows a single merged cell instead of one column per day.</summary>
    [ObservableProperty]
    private bool _isCollapsed = true;

    /// <summary>True when this block's merged view should consider/write the given day cell.</summary>
    public bool IsRelevant(RosterDayCellViewModel cell) => _isRelevant(cell);
}
