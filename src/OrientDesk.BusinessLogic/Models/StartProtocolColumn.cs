namespace OrientDesk.BusinessLogic.Models;

/// <summary>
/// A column that can appear in a start protocol, in a stable identity that survives serialisation (the
/// user's chosen column set + order is persisted per day). The start-protocol builder maps each selected
/// column to a cell value per participant row. Distinct from <see cref="ProtocolColumn"/> because a start
/// protocol shows start-time / chip / group rather than result / place / score.
/// </summary>
public enum StartProtocolColumn
{
    /// <summary>Start time («Старт»), e.g. "10:23". The defining column of a start protocol.</summary>
    StartTime,

    /// <summary>1-based sequence within the section (the «№ з/п» column).</summary>
    Sequence,

    /// <summary>Bib / start number («Номер»).</summary>
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

    /// <summary>Chip / SI-card number («Чіп»).</summary>
    Chip,

    /// <summary>Group name («Група») — useful in the judges' protocol, where a minute section mixes groups.</summary>
    Group
}
