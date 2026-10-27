using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.Presentation.ViewModels.Pages;

/// <summary>
/// Indexer over a result's discipline-specific values (<see cref="ParticipantDayResult.Extra"/>), formatted by the
/// discipline's column definitions, so a grid cell can bind <c>ResultExtras[key]</c>. A missing value reads as empty.
/// </summary>
public sealed class ResultExtrasView
{
    private readonly ParticipantDayResult _result;
    private readonly IReadOnlyList<ResultColumnDef> _columns;

    public ResultExtrasView(ParticipantDayResult result, IReadOnlyList<ResultColumnDef> columns)
    {
        _result = result;
        _columns = columns;
    }

    public string this[string key]
    {
        get
        {
            if (_result.Extra.Count == 0)
                return string.Empty;
            var column = _columns.FirstOrDefault(c => c.Key == key);
            if (column is null)
                return _result.Extra.TryGetValue(key, out var raw) ? raw?.ToString() ?? string.Empty : string.Empty;
            return column.FormatValue(new ResultRow(new Dictionary<string, object?>(_result.Extra)));
        }
    }
}

/// <summary>Whether a plugin result column flags this result's value, by column key (false for an unknown key).</summary>
public sealed class ResultProblemsView
{
    private readonly ParticipantDayResult _result;
    private readonly IReadOnlyList<ResultColumnDef> _columns;

    public ResultProblemsView(ParticipantDayResult result, IReadOnlyList<ResultColumnDef> columns)
    {
        _result = result;
        _columns = columns;
    }

    public bool this[string key] =>
        _result.Extra.Count > 0
        && _columns.FirstOrDefault(c => c.Key == key) is { } column
        && column.IsProblem(new ResultRow(new Dictionary<string, object?>(_result.Extra)));
}
