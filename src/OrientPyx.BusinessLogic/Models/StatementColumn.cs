namespace OrientPyx.BusinessLogic.Models;

/// <summary>
/// A column that can appear in a participant statement («відомість»), in a stable identity that survives
/// serialisation (the user's chosen column set + order is persisted per competition and as an app-level
/// default). The statement builder maps each selected column to a cell value per participant row. Unlike the
/// results protocol, the statement is a flat identity list — no result/place/score columns — but adds the
/// per-day membership fields (group, chip) and the extra identity fields (representative, ФСОУ code, note).
/// </summary>
public enum StatementColumn
{
    /// <summary>1-based sequence within the whole list (the «№ з/п» column).</summary>
    Sequence,

    /// <summary>Bib / start number.</summary>
    Number,

    /// <summary>Full name («Прізвище, ім'я»).</summary>
    FullName,

    /// <summary>Birth date («Дата народження»).</summary>
    BirthDate,

    /// <summary>Group («Група»). On the roster (all days) the distinct per-day values are joined with " / ".</summary>
    Group,

    /// <summary>Chip number («Чіп»). Own chips print bold; rental chips normal. On the roster the distinct
    /// per-day chips are joined with " / ".</summary>
    Chip,

    /// <summary>Start time («Старт»). Unlike the other columns this is a <b>per-day block</b>: at build time the
    /// single logical column expands into one physical column per competition day (headed "Старт (Д1)",
    /// "Старт (Д2)"…). In single-day scope (day mode, or a one-day competition) it collapses to one plain
    /// "Старт" column. Empty cells for a day the participant does not run / has no start time.</summary>
    Start,

    /// <summary>Region («Регіон»).</summary>
    Region,

    /// <summary>Club («Клуб»).</summary>
    Club,

    /// <summary>Sports school / ДЮСШ.</summary>
    Dussh,

    /// <summary>Coach («Тренер»).</summary>
    Coach,

    /// <summary>Sports rank («Кваліфікація»).</summary>
    Rank,

    /// <summary>Team («Команда»).</summary>
    Team,

    /// <summary>Representative («Представник»).</summary>
    Representative,

    /// <summary>ФСОУ code («Код ФСОУ»).</summary>
    FsouCode,

    /// <summary>Free note («Примітка»).</summary>
    Note
}
