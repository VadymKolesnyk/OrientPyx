namespace OrientDesk.BusinessLogic.Models;

/// <summary>
/// A configurable <b>leading</b> (identity) column of the multi-day summary protocol, in a stable identity that
/// survives serialisation. These are the columns to the LEFT of the per-day result bands — the user can add /
/// hide them and change their order; the per-day bands (М / Час / Очки) and the trailing «Сума» are always last
/// and are not part of this set. The place («Місце») within the group is the <see cref="Sequence"/> column.
/// </summary>
public enum SummaryColumn
{
    /// <summary>Place within the group section («Місце»).</summary>
    Sequence,

    /// <summary>Bib / start number.</summary>
    Number,

    /// <summary>Full name («Прізвище, ім'я»).</summary>
    FullName,

    /// <summary>Birth date («ДН»).</summary>
    BirthDate,

    /// <summary>Region («Регіон»).</summary>
    Region,

    /// <summary>Club («Клуб»).</summary>
    Club,

    /// <summary>Sports school / ДЮСШ.</summary>
    Dussh,

    /// <summary>Coach («Тренер»).</summary>
    Coach,

    /// <summary>Sports rank / qualification («Кваліфікація»).</summary>
    Rank
}
