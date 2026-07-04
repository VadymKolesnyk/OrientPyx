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
    private bool _isActive;

    [ObservableProperty]
    private bool _isDirty;

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

    /// <summary>"Day 1"-style label for the row.</summary>
    public string NumberLabel => $"{Localization.Get("Header.Day")} {Number}";

    public EventDay ToEntity() => new()
    {
        Id = _id,
        Number = Number,
        Date = Date,
        Venue = (Venue ?? string.Empty).Trim(),
        DefaultDiscipline = SelectedDiscipline.Value,
        CreatedAt = _createdAt
    };

    public void MarkSaved() => IsDirty = false;

    partial void OnDateChanged(DateTimeOffset? value) => IsDirty = true;
    partial void OnVenueChanged(string value) => IsDirty = true;
    partial void OnSelectedDisciplineChanged(DisciplineTypeOption value) => IsDirty = true;

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e)
        => OnPropertyChanged(nameof(NumberLabel));
}
