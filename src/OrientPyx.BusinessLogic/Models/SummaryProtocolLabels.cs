namespace OrientPyx.BusinessLogic.Models;

/// <summary>
/// The localized strings the summary-protocol builder needs, so it stays free of <c>ILocalizationService</c>.
/// Supplied by the Presentation layer from the resource dictionary.
/// </summary>
/// <param name="DefaultTitle">Fallback main title when the settings leave it blank.</param>
/// <param name="DayBand">Format for a day-band caption, "{0}" = day number, "{1}" = date ("День {0} ({1})").</param>
/// <param name="ColSequence">Leading column «Місце» (place within the group).</param>
/// <param name="ColNumber">Leading column «Номер» (bib / start number).</param>
/// <param name="ColFullName">Leading column «Прізвище, ім'я».</param>
/// <param name="ColBirthDate">Leading column «ДН» (birth date).</param>
/// <param name="ColRegion">Leading column «Регіон».</param>
/// <param name="ColClub">Leading column «Клуб».</param>
/// <param name="ColDussh">Leading column «ДЮСШ».</param>
/// <param name="ColCoach">Leading column «Тренер».</param>
/// <param name="ColRank">Leading column «Кваліфікація».</param>
/// <param name="SubPlace">Per-day sub-column «М» (place that day).</param>
/// <param name="SubTime">Per-day sub-column «Час» (result time that day).</param>
/// <param name="SubPoints">Per-day sub-column «Очки» (points that day; points mode only).</param>
/// <param name="Total">Trailing column «Сума».</param>
/// <param name="ChiefJudge">Officials signature-block role.</param>
/// <param name="ChiefSecretary">Officials signature-block role.</param>
/// <param name="Jury">Officials signature-block role.</param>
/// <param name="FooterSoftwareName">Page footer software name.</param>
/// <param name="FooterGeneratedLabel">Page footer "generated" caption.</param>
/// <param name="FooterPageLabel">Page footer "page" caption.</param>
public sealed record SummaryProtocolLabels(
    string DefaultTitle,
    string DayBand,
    string ColSequence,
    string ColNumber,
    string ColFullName,
    string ColBirthDate,
    string ColRegion,
    string ColClub,
    string ColDussh,
    string ColCoach,
    string ColRank,
    string SubPlace,
    string SubTime,
    string SubPoints,
    string Total,
    string ChiefJudge,
    string ChiefSecretary,
    string Jury,
    string FooterSoftwareName,
    string FooterGeneratedLabel,
    string FooterPageLabel);
