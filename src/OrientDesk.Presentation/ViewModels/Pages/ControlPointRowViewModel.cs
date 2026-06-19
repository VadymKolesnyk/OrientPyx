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

    // Paper-map position (mm) + scale captured at import; used to render the read-only "by map"
    // ground-metre columns. Not edited here, so kept as plain fields and round-tripped via ToEntity.
    private readonly double? _mapX;
    private readonly double? _mapY;
    private readonly int? _mapScale;

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

    [ObservableProperty]
    private string _pointsText;

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
        _mapX = point.MapX;
        _mapY = point.MapY;
        _mapScale = point.MapScale;
        Localization = localization;

        TypeOptions = Enum.GetValues<ControlPointType>()
            .Select(t => new ControlPointTypeOption(t, localization))
            .ToList();

        _code = point.Code;
        _latitudeText = FormatCoord(point.Latitude);
        _longitudeText = FormatCoord(point.Longitude);
        _pointsText = FormatPoints(point.Points);
        _selectedType = TypeOptions.First(o => o.Value == point.Type);

        _initialized = true;
    }

    public ILocalizationService Localization { get; }

    public Guid Id => _id;

    /// <summary>
    /// Read-only ground X in metres for the "by map" coordinate mode: paper millimetres scaled by
    /// the map scale (mm × scale ÷ 1000). Empty when the row has no map position or no scale.
    /// </summary>
    public string MapXText => FormatMapMetres(_mapX);

    /// <summary>Read-only ground Y in metres for the "by map" coordinate mode (see <see cref="MapXText"/>).</summary>
    public string MapYText => FormatMapMetres(_mapY);

    private string FormatMapMetres(double? mm)
    {
        if (mm is null || _mapScale is null or 0)
            return string.Empty;

        var metres = mm.Value * _mapScale.Value / 1000.0;
        return metres.ToString("0.#", CultureInfo.InvariantCulture);
    }

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
        MapX = _mapX,
        MapY = _mapY,
        MapScale = _mapScale,
        Type = SelectedType.Value,
        Points = ParsePoints(PointsText),
        CreatedAt = _createdAt
    };

    partial void OnCodeChanged(string value) => QueueSave();
    partial void OnLatitudeTextChanged(string value) => QueueSave();
    partial void OnLongitudeTextChanged(string value) => QueueSave();
    partial void OnPointsTextChanged(string value) => QueueSave();
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

    private static string FormatPoints(int? value)
        => value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    private static int? ParsePoints(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        return int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }
}
