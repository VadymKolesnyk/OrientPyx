using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.Localization;

namespace OrientPyx.Presentation.ViewModels.Pages;

/// <summary>
/// One editable row in the ranks grid (application-level "Розряди" settings table). Wraps a single
/// <see cref="SportRank"/>: a name and its points. Both edit in the background (debounced per row) via
/// the page-supplied <c>requestSave</c> callback. Points are held as text so a half-typed number does
/// not snap to 0 (mirrors the group distance field).
/// </summary>
public sealed partial class RankRowViewModel : ObservableObject
{
    private readonly Guid _id;
    private int _order;
    private readonly Action<RankRowViewModel> _requestSave;

    private bool _initialized;

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string _pointsText;

    public RankRowViewModel(
        SportRank rank,
        ILocalizationService localization,
        Action<RankRowViewModel> requestSave)
    {
        _id = rank.Id;
        _order = rank.Order;
        _requestSave = requestSave;
        Localization = localization;

        _name = rank.Name;
        _pointsText = FormatPoints(rank.Points);

        _initialized = true;
    }

    /// <summary>True while <see cref="Apply"/> copies fresh values onto the row; the page skips its save callbacks then.</summary>
    public bool IsApplying { get; private set; }

    /// <summary>Copies a freshly-read rank onto this row without triggering a save (only changed cells raise).</summary>
    public void Apply(SportRank rank)
    {
        IsApplying = true;
        _initialized = false;
        try
        {
            _order = rank.Order;
            Name = rank.Name;
            PointsText = FormatPoints(rank.Points);
        }
        finally
        {
            _initialized = true;
            IsApplying = false;
        }
    }

    public ILocalizationService Localization { get; }

    public Guid Id => _id;

    public SportRank ToEntity() => new()
    {
        Id = _id,
        Name = (Name ?? string.Empty).Trim(),
        Points = ParsePoints(PointsText),
        Order = _order
    };

    partial void OnNameChanged(string value) => QueueSave();
    partial void OnPointsTextChanged(string value) => QueueSave();

    private void QueueSave()
    {
        if (_initialized)
            _requestSave(this);
    }

    private static string FormatPoints(double value)
        => value.ToString("0.######", CultureInfo.InvariantCulture);

    private static double ParsePoints(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        var normalized = text.Trim().Replace(',', '.');
        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;
    }
}
