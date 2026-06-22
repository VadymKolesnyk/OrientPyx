namespace OrientDesk.BusinessLogic.Models;

/// <summary>
/// A column that can appear in a results protocol, in a stable identity that survives serialisation
/// (the user's chosen column set + order is persisted at the application level). The protocol builder
/// maps each selected column to a cell value per participant row; columns whose source data does not yet
/// exist (e.g. ranking points, course setter) are intentionally absent — only data the app actually has.
/// </summary>
public enum ProtocolColumn
{
    /// <summary>1-based sequence within the group section (the «№ з/п» column).</summary>
    Sequence,

    /// <summary>Bib / start number.</summary>
    Number,

    /// <summary>Full name («Прізвище, ім'я»).</summary>
    FullName,

    /// <summary>Birth date («Дата народження»).</summary>
    BirthDate,

    /// <summary>Club («Клуб»).</summary>
    Club,

    /// <summary>Region («Регіон»).</summary>
    Region,

    /// <summary>Sports school / ДЮСШ.</summary>
    Dussh,

    /// <summary>Coach («Тренер»).</summary>
    Coach,

    /// <summary>Sports rank («Кваліфікація»).</summary>
    Rank,

    /// <summary>Result time («Результат»), or the status code (DNS/DNF/MP…) when not a clean finish.</summary>
    Result,

    /// <summary>Place within the group («Місце»).</summary>
    Place,

    /// <summary>Score / points for a point-scoring discipline («Бали»).</summary>
    Score,

    /// <summary>Ranking points awarded by the group's points rule («Очки»).</summary>
    Points,

    /// <summary>The awarded sports rank computed from the result («Виконаний розряд», Додаток 89).</summary>
    AwardedRank
}
