namespace OrientDesk.BusinessLogic.Models;

/// <summary>
/// The set of columns a per-group results table can show, shared by the online live-results publish
/// (<see cref="OnlinePublishSettings"/>) and the on-screen monitor (<see cref="MonitorSettings"/>). Every
/// value maps onto a field already present on <see cref="OnlineResultRow"/>, so the same computed snapshot
/// drives both surfaces. The string key (<see cref="ResultColumnDef"/>) is what is stored in the JSON config
/// and — for the online side — sent to the frontend's <c>display_config</c>, so it must stay stable.
/// </summary>
public enum ResultColumn
{
    /// <summary>Place within the group («Місце»).</summary>
    Place,
    /// <summary>Full name («Прізвище, ім'я»).</summary>
    FullName,
    /// <summary>Start number / bib («Номер»).</summary>
    Bib,
    /// <summary>Birth year/date («Рік»).</summary>
    Birth,
    /// <summary>Qualification / rank («Розряд»).</summary>
    Qual,
    /// <summary>Team («Команда»).</summary>
    Team,
    /// <summary>Club («Колектив»).</summary>
    Club,
    /// <summary>Region («Регіон»).</summary>
    Region,
    /// <summary>Actual start time of day («Старт»).</summary>
    StartTime,
    /// <summary>Result time («Результат»).</summary>
    ResultTime,
    /// <summary>Loss to the group leader («Відставання»).</summary>
    Gap,
    /// <summary>Status / reason for an unplaced run, e.g. DNS/MP («Статус»).</summary>
    Status,
    /// <summary>Ranking points / rogaine score («Очки»/«Бали»).</summary>
    Points,
}

/// <summary>
/// Static metadata for a <see cref="ResultColumn"/>: its stable string key (used in stored JSON and in the
/// frontend's <c>display_config</c>) and the localization key for its header label. The catalogue
/// (<see cref="All"/>) is the single source of truth for what columns exist and their default order.
/// </summary>
public sealed record ResultColumnDef(ResultColumn Column, string Key, string LabelKey)
{
    /// <summary>Every available column, in the natural default order. Keys mirror the spectator frontend's
    /// own column ids (rk / full_name / bib / …), so the online publish can hand them through unchanged.</summary>
    public static readonly IReadOnlyList<ResultColumnDef> All =
    [
        new(ResultColumn.Place,      "rk",          "ResultColumn.Place"),
        new(ResultColumn.FullName,   "full_name",   "ResultColumn.FullName"),
        new(ResultColumn.Bib,        "bib",         "ResultColumn.Bib"),
        new(ResultColumn.Birth,      "birth",       "ResultColumn.Birth"),
        new(ResultColumn.Qual,       "qual",        "ResultColumn.Qual"),
        new(ResultColumn.Team,       "team",        "ResultColumn.Team"),
        new(ResultColumn.Club,       "club",        "ResultColumn.Club"),
        new(ResultColumn.Region,     "region",      "ResultColumn.Region"),
        new(ResultColumn.StartTime,  "start_time",  "ResultColumn.StartTime"),
        new(ResultColumn.ResultTime, "result_time", "ResultColumn.ResultTime"),
        new(ResultColumn.Gap,        "gap",         "ResultColumn.Gap"),
        new(ResultColumn.Status,     "status",      "ResultColumn.Status"),
        new(ResultColumn.Points,     "points",      "ResultColumn.Points"),
    ];

    /// <summary>Looks up a column by its stable key, or null when the key is unknown (e.g. a removed column
    /// in an old saved config).</summary>
    public static ResultColumnDef? ByKey(string key) =>
        All.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase));
}
