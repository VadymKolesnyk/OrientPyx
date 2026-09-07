using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Enums;
using OrientPyx.Localization;

namespace OrientPyx.Presentation.ViewModels.Pages;

/// <summary>One editable row in the days table. Wraps a single <see cref="EventDay"/>.</summary>
public sealed partial class DayRowViewModel : ObservableObject
{
    private readonly Guid _id;
    private readonly DateTimeOffset _createdAt;

    [ObservableProperty]
    private DateTimeOffset? _date;

    [ObservableProperty]
    private string _venue;

    [ObservableProperty]
    private DisciplineTypeOption _selectedDiscipline;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDelete))]
    private bool _isActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private bool _isDirty;

    /// <summary>
    /// True while the day is closed for editing. Editable straight from the days grid, which is the one
    /// place all days are visible at once; the same lock also lives next to the day selector on every
    /// per-day page.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LockedDayNumber))]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    [NotifyPropertyChangedFor(nameof(CanDelete))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private bool _isLocked;

    /// <summary>
    /// This row's own day number while it is closed, else 0 — what the grid's cells bind to for their
    /// per-row lock. The days grid is the one table whose ROWS are days, so a table-wide lock can't
    /// express it: each row carries its own state.
    /// </summary>
    public int LockedDayNumber => IsLocked ? Number : 0;

    /// <summary>False while the day is closed — the row's Save / «змінити номер» actions turn off with it.</summary>
    public bool CanEdit => !IsLocked;

    /// <summary>
    /// Whether the row's delete button is live: never for the day in use, and never for a closed one
    /// (deleting a finished day is precisely what the lock is there to prevent).
    /// </summary>
    public bool CanDelete => !IsActive && !IsLocked;

    /// <summary>Save is offered only for a row with unsaved edits, and only while the day is open.</summary>
    public bool CanSave => IsDirty && !IsLocked;

    public DayRowViewModel(EventDay day, bool isActive, ILocalizationService localization,
        string venuePlaceholder = "")
    {
        _id = day.Id;
        _createdAt = day.CreatedAt;
        Number = day.Number;
        _date = day.Date;
        _venue = day.Venue;
        VenuePlaceholder = venuePlaceholder;
        Localization = localization;

        DisciplineOptions = Enum.GetValues<DisciplineType>()
            .Select(t => new DisciplineTypeOption(t, localization))
            .ToList();
        _selectedDiscipline = DisciplineOptions.First(o => o.Value == day.DefaultDiscipline);

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
    public IReadOnlyList<DisciplineTypeOption> DisciplineOptions { get; }

    /// <summary>1-based day number (immutable label).</summary>
    public int Number { get; }

    public string NumberLabel => $"{Localization.Get("Header.Day")} {Number}";

    public EventDay ToEntity() => new()
    {
        Id = _id,
        Number = Number,
        Date = Date,
        Venue = (Venue ?? string.Empty).Trim(),
        DefaultDiscipline = SelectedDiscipline.Value,
        IsLocked = IsLocked,
        CreatedAt = _createdAt
    };

    public void MarkSaved() => IsDirty = false;

    partial void OnDateChanged(DateTimeOffset? value) => IsDirty = true;
    partial void OnVenueChanged(string value) => IsDirty = true;
    partial void OnSelectedDisciplineChanged(DisciplineTypeOption value) => IsDirty = true;

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e)
        => OnPropertyChanged(nameof(NumberLabel));
}
