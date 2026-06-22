namespace OrientDesk.BusinessLogic.Enums;

/// <summary>
/// Which sports-rank level a group awards on a day (Додаток 89, п.6 — simplified). Picked per group and
/// stored as a string in the event database; drives which columns of the qualification table apply when
/// the awarded rank is computed.
/// </summary>
public enum GroupRankLevel
{
    /// <summary>No ranks are awarded for this group.</summary>
    None,

    /// <summary>Adult ranks (МСУ, КМСУ, I, II, III).</summary>
    Adult,

    /// <summary>Junior ranks (I-ю, II-ю, III-ю) — МС/КМС/I are not available.</summary>
    Junior
}
