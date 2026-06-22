using CommunityToolkit.Mvvm.ComponentModel;
using OrientDesk.BusinessLogic.Models;

namespace OrientDesk.Presentation.ViewModels.Pages;

/// <summary>
/// One place→points row in the placement-table editor (right-hand side of the Points page when a table
/// rule is selected). The place is fixed (1 = winner); the points are editable as text so a half-typed
/// number does not snap to 0. Edits notify the page via <c>onChanged</c> so it can persist the table.
/// </summary>
public sealed partial class PointsTableRowViewModel : ObservableObject
{
    private readonly Action _onChanged;
    private readonly bool _initialized;

    public PointsTableRowViewModel(int place, decimal points, Action onChanged)
    {
        Place = place;
        _onChanged = onChanged;
        _pointsText = PointsTable.Format(points);
        _initialized = true;
    }

    /// <summary>The placement this row scores (1 = first place).</summary>
    public int Place { get; }

    [ObservableProperty]
    private string _pointsText;

    /// <summary>The parsed points value (rounded to two decimals).</summary>
    public decimal Points => PointsTable.ParseValue(PointsText);

    partial void OnPointsTextChanged(string value)
    {
        if (_initialized)
            _onChanged();
    }
}
