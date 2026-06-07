using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using OrientDesk.BusinessLogic.Entities;
using OrientDesk.Localization;

namespace OrientDesk.Presentation.ViewModels.Pages;

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
    private string _discipline;

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private bool _isDirty;

    public DayRowViewModel(EventDay day, bool isActive, ILocalizationService localization)
    {
        _id = day.Id;
        _createdAt = day.CreatedAt;
        Number = day.Number;
        _date = day.Date;
        _venue = day.Venue;
        _discipline = day.Discipline;
        _isActive = isActive;
        Localization = localization;
        Localization.PropertyChanged += OnLocalizationChanged;
    }

    public ILocalizationService Localization { get; }

    public Guid Id => _id;

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
        Discipline = (Discipline ?? string.Empty).Trim(),
        CreatedAt = _createdAt
    };

    public void MarkSaved() => IsDirty = false;

    partial void OnDateChanged(DateTimeOffset? value) => IsDirty = true;
    partial void OnVenueChanged(string value) => IsDirty = true;
    partial void OnDisciplineChanged(string value) => IsDirty = true;

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e)
        => OnPropertyChanged(nameof(NumberLabel));
}
