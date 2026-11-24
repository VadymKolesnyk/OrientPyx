using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.Localization;

namespace OrientPyx.Presentation.ViewModels.Pages;

/// <summary>
/// One editable row in the days table. Wraps a single <see cref="EventDay"/>. An edit of the date, venue or discipline
/// asks the page to save the row (debounced there) — there is no Save button.
/// </summary>
public sealed partial class DayRowViewModel : ObservableObject
{
    private readonly Guid _id;
    private DateTimeOffset _createdAt;
    private readonly IDisciplineStrategyProvider _strategies;
    // The page's debounced save; called on every edit of an editable cell (never while Apply copies stored values).
    private readonly Action<DayRowViewModel> _requestSave;
    // Suppresses the dirty flag (and the save) while Apply copies stored values onto the row.
    private bool _applying;
    // Bumped on every edit; a save that read the row at an older version leaves the row dirty (typed on meanwhile).
    private int _editVersion;
    // The last discipline the combo held: what a save writes while the combo is momentarily cleared.
    private DisciplineId _discipline;

    [ObservableProperty]
    private DateTimeOffset? _date;

    [ObservableProperty]
    private string _venue;

    [ObservableProperty]
    private DisciplineTypeOption _selectedDiscipline;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDelete))]
    private bool _isActive;

    /// <summary>True while the row holds an edit not yet written — a refresh leaves its cells alone then.</summary>
    public bool IsDirty { get; private set; }

    /// <summary>
    /// True while the day is closed for editing. Editable straight from the days grid, which is the one
    /// place all days are visible at once; the same lock also lives next to the day selector on every
    /// per-day page.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LockedDayNumber))]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    [NotifyPropertyChangedFor(nameof(CanDelete))]
    private bool _isLocked;

    /// <summary>
    /// This row's own day number while it is closed, else 0 — what the grid's cells bind to for their
    /// per-row lock. The days grid is the one table whose ROWS are days, so a table-wide lock can't
    /// express it: each row carries its own state.
    /// </summary>
    public int LockedDayNumber => IsLocked ? Number : 0;

    /// <summary>False while the day is closed — the row's «змінити номер» action turns off with it.</summary>
    public bool CanEdit => !IsLocked;

    /// <summary>
    /// Whether the row's delete button is live: never for the day in use, and never for a closed one
    /// (deleting a finished day is precisely what the lock is there to prevent).
    /// </summary>
    public bool CanDelete => !IsActive && !IsLocked;

    public DayRowViewModel(EventDay day, bool isActive, ILocalizationService localization,
        IDisciplineStrategyProvider strategies, Action<DayRowViewModel> requestSave, string venuePlaceholder = "")
    {
        _id = day.Id;
        _requestSave = requestSave;
        _createdAt = day.CreatedAt;
        _strategies = strategies;
        Number = day.Number;
        _date = day.Date;
        _venue = day.Venue;
        VenuePlaceholder = venuePlaceholder;
        Localization = localization;

        _disciplineOptions = DisciplineTypeOption.ListFor(strategies, localization, day.DefaultDiscipline);
        _selectedDiscipline = DisciplineOptions.First(o => o.Value == day.DefaultDiscipline);
        _discipline = day.DefaultDiscipline;

        _isActive = isActive;
        _isLocked = day.IsLocked;
        Localization.PropertyChanged += OnLocalizationChanged;
    }

    public ILocalizationService Localization { get; }

    /// <summary>The competition's own venue, shown (greyed) as the cell watermark while this day's venue is
    /// blank — so an empty day venue reads as the competition venue, which the protocols inherit too.</summary>
    public string VenuePlaceholder { get; }

    public Guid Id => _id;

    /// <summary>Discipline options (value + localized label) shown in the Type ComboBox.</summary>
    [ObservableProperty]
    private IReadOnlyList<DisciplineTypeOption> _disciplineOptions;

    /// <summary>1-based day number (a label; renumbering goes through the editor and comes back on reload).</summary>
    public int Number { get; private set; }

    public string NumberLabel => $"{Localization.Get("Header.Day")} {Number}";

    /// <summary>
    /// Copies a freshly-read day onto this row. The editable cells (date, venue, discipline) are left alone while
    /// the row holds an edit not yet saved — the operator's typing wins over a refresh — and never mark the row dirty;
    /// the lock and the active flag always follow the store. Only a changed cell raises a property change.
    /// </summary>
    public void Apply(EventDay day, bool isActive)
    {
        _applying = true;
        try
        {
            _createdAt = day.CreatedAt;
            if (Number != day.Number)
            {
                Number = day.Number;
                OnPropertyChanged(nameof(Number));
                OnPropertyChanged(nameof(NumberLabel));
                OnPropertyChanged(nameof(LockedDayNumber));
            }
            IsActive = isActive;
            IsLocked = day.IsLocked;

            if (IsDirty)
                return;

            Date = day.Date;
            Venue = day.Venue;
            if (DisciplineOptions.All(o => o.Value != day.DefaultDiscipline))
                DisciplineOptions = DisciplineTypeOption.ListFor(_strategies, Localization, day.DefaultDiscipline);
            SelectedDiscipline = DisciplineOptions.First(o => o.Value == day.DefaultDiscipline);
        }
        finally
        {
            _applying = false;
        }
    }

    public EventDay ToEntity() => new()
    {
        Id = _id,
        Number = Number,
        Date = Date,
        Venue = (Venue ?? string.Empty).Trim(),
        DefaultDiscipline = SelectedDiscipline?.Value ?? _discipline,
        IsLocked = IsLocked,
        CreatedAt = _createdAt
    };

    /// <summary>The edit counter, read together with <see cref="ToEntity"/> when a save starts.</summary>
    public int EditVersion => _editVersion;

    /// <summary>A save of the row as it stood at <paramref name="version"/> landed: the row is clean unless it was
    /// edited again meanwhile (that edit has its own save queued).</summary>
    public void MarkSaved(int version)
    {
        if (version == _editVersion)
            IsDirty = false;
    }

    /// <summary>The store refused the edit (the day was closed meanwhile): the next refresh shows the stored values.</summary>
    public void DiscardEdits() => IsDirty = false;

    partial void OnDateChanged(DateTimeOffset? value) => Edited();
    partial void OnVenueChanged(string value) => Edited();
    // A combo clears its selection (null) when its list is swapped under it — that is no choice, so nothing to save.
    partial void OnSelectedDisciplineChanged(DisciplineTypeOption value)
    {
        if (value is null)
            return;
        _discipline = value.Value;
        Edited();
    }

    private void Edited()
    {
        if (_applying)
            return;
        _editVersion++;
        IsDirty = true;
        _requestSave(this);
    }

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e)
        => OnPropertyChanged(nameof(NumberLabel));
}
