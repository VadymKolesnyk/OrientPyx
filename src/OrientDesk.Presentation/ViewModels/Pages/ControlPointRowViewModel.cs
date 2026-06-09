using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using OrientDesk.BusinessLogic.Entities;
using OrientDesk.BusinessLogic.Enums;
using OrientDesk.Localization;

namespace OrientDesk.Presentation.ViewModels.Pages;

/// <summary>
/// One editable row in the control-points grid. Wraps a single <see cref="ControlPoint"/>.
/// Edits do not save the row directly — each change invokes the page-supplied
/// <c>requestSave</c> callback, which debounces and persists in the background.
/// </summary>
public sealed partial class ControlPointRowViewModel : ObservableObject
{
    private readonly Guid _id;
    private readonly Guid _eventDayId;
    private readonly int _order;
    private readonly DateTimeOffset _createdAt;
    private readonly Action<ControlPointRowViewModel> _requestSave;

    // Suppresses save requests while the constructor seeds initial values.
    private readonly bool _initialized;

    [ObservableProperty]
    private string _code;

    [ObservableProperty]
    private string _latitudeText;

    [ObservableProperty]
    private string _longitudeText;

    [ObservableProperty]
    private ControlPointTypeOption _selectedType;

    public ControlPointRowViewModel(
        ControlPoint point,
        ILocalizationService localization,
        Action<ControlPointRowViewModel> requestSave)
    {
        _id = point.Id;
        _eventDayId = point.EventDayId;
        _order = point.Order;
        _createdAt = point.CreatedAt;
        _requestSave = requestSave;
        Localization = localization;

        TypeOptions = Enum.GetValues<ControlPointType>()
            .Select(t => new ControlPointTypeOption(t, localization))
            .ToList();

        _code = point.Code;
        _latitudeText = FormatCoord(point.Latitude);
        _longitudeText = FormatCoord(point.Longitude);
        _selectedType = TypeOptions.First(o => o.Value == point.Type);

        _initialized = true;
    }

    public ILocalizationService Localization { get; }

    public Guid Id => _id;

    /// <summary>Type options (value + localized label) shown in the Type ComboBox.</summary>
    public IReadOnlyList<ControlPointTypeOption> TypeOptions { get; } = [];

    public ControlPoint ToEntity() => new()
    {
        Id = _id,
        EventDayId = _eventDayId,
        Order = _order,
        Code = (Code ?? string.Empty).Trim(),
        Latitude = ParseCoord(LatitudeText),
        Longitude = ParseCoord(LongitudeText),
        Type = SelectedType.Value,
        CreatedAt = _createdAt
    };

    partial void OnCodeChanged(string value) => QueueSave();
    partial void OnLatitudeTextChanged(string value) => QueueSave();
    partial void OnLongitudeTextChanged(string value) => QueueSave();
    partial void OnSelectedTypeChanged(ControlPointTypeOption value) => QueueSave();

    private void QueueSave()
    {
        if (_initialized)
            _requestSave(this);
    }

    private static string FormatCoord(double? value)
        => value?.ToString("0.######", CultureInfo.InvariantCulture) ?? string.Empty;

    private static double? ParseCoord(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        // Accept both comma and dot as the decimal separator; store canonically.
        var normalized = text.Trim().Replace(',', '.');
        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }
}
