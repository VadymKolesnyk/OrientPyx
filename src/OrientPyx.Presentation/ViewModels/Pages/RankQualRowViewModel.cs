using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.Localization;

namespace OrientPyx.Presentation.ViewModels.Pages;

/// <summary>
/// One editable row of the rank qualification table (Додаток 89): a course-rank threshold and the ten
/// percentage cells (five time, five points). Cells are held as text so a half-typed number does not snap,
/// and an empty cell means "rank not attainable" (stored as null). Edits save in the background (debounced
/// per row) via the page-supplied <c>requestSave</c> callback.
/// </summary>
public sealed partial class RankQualRowViewModel : ObservableObject
{
    private readonly Guid _id;
    private readonly int _order;
    private readonly Action<RankQualRowViewModel> _requestSave;
    private readonly bool _initialized;

    [ObservableProperty] private string _rankText;
    [ObservableProperty] private string _timeKmsText;
    [ObservableProperty] private string _timeFirstText;
    [ObservableProperty] private string _timeSecondText;
    [ObservableProperty] private string _timeThirdText;
    [ObservableProperty] private string _timeThirdJuniorText;
    [ObservableProperty] private string _pointsKmsText;
    [ObservableProperty] private string _pointsFirstText;
    [ObservableProperty] private string _pointsSecondText;
    [ObservableProperty] private string _pointsThirdText;
    [ObservableProperty] private string _pointsThirdJuniorText;

    public RankQualRowViewModel(
        RankQualificationRow row,
        ILocalizationService localization,
        Action<RankQualRowViewModel> requestSave)
    {
        _id = row.Id;
        _order = row.Order;
        _requestSave = requestSave;
        Localization = localization;

        _rankText = row.Rank.ToString(CultureInfo.InvariantCulture);
        _timeKmsText = Format(row.TimeKms);
        _timeFirstText = Format(row.TimeFirst);
        _timeSecondText = Format(row.TimeSecond);
        _timeThirdText = Format(row.TimeThird);
        _timeThirdJuniorText = Format(row.TimeThirdJunior);
        _pointsKmsText = Format(row.PointsKms);
        _pointsFirstText = Format(row.PointsFirst);
        _pointsSecondText = Format(row.PointsSecond);
        _pointsThirdText = Format(row.PointsThird);
        _pointsThirdJuniorText = Format(row.PointsThirdJunior);

        _initialized = true;
    }

    public ILocalizationService Localization { get; }

    public Guid Id => _id;

    public RankQualificationRow ToEntity() => new()
    {
        Id = _id,
        Order = _order,
        Rank = ParseInt(RankText) ?? 0,
        TimeKms = ParseInt(TimeKmsText),
        TimeFirst = ParseInt(TimeFirstText),
        TimeSecond = ParseInt(TimeSecondText),
        TimeThird = ParseInt(TimeThirdText),
        TimeThirdJunior = ParseInt(TimeThirdJuniorText),
        PointsKms = ParseInt(PointsKmsText),
        PointsFirst = ParseInt(PointsFirstText),
        PointsSecond = ParseInt(PointsSecondText),
        PointsThird = ParseInt(PointsThirdText),
        PointsThirdJunior = ParseInt(PointsThirdJuniorText),
    };

    partial void OnRankTextChanged(string value) => QueueSave();
    partial void OnTimeKmsTextChanged(string value) => QueueSave();
    partial void OnTimeFirstTextChanged(string value) => QueueSave();
    partial void OnTimeSecondTextChanged(string value) => QueueSave();
    partial void OnTimeThirdTextChanged(string value) => QueueSave();
    partial void OnTimeThirdJuniorTextChanged(string value) => QueueSave();
    partial void OnPointsKmsTextChanged(string value) => QueueSave();
    partial void OnPointsFirstTextChanged(string value) => QueueSave();
    partial void OnPointsSecondTextChanged(string value) => QueueSave();
    partial void OnPointsThirdTextChanged(string value) => QueueSave();
    partial void OnPointsThirdJuniorTextChanged(string value) => QueueSave();

    private void QueueSave()
    {
        if (_initialized)
            _requestSave(this);
    }

    private static string Format(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    private static int? ParseInt(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        return int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }
}
