namespace OrientPyx.BusinessLogic.Models;

/// <summary>
/// The localized strings the summary-protocol builder needs, so it stays free of <c>ILocalizationService</c>.
/// Supplied by the Presentation layer from the resource dictionary.
/// </summary>
/// <param name="DefaultTitle">Fallback main title when the settings leave it blank.</param>
/// <param name="DayBand">Format for a day-band caption, "{0}" = day number, "{1}" = date ("День {0} ({1})").</param>
/// <remarks>
/// <c>Col*</c> are the leading columns («Місце», «Номер», «Прізвище, ім'я», «ДН», «Регіон», «Клуб»,
/// «ДЮСШ», «Тренер», «Кваліфікація»); <c>Sub*</c> the per-day sub-columns («М» place, «Час» time,
/// «Очки» points — points mode only); <c>Total</c> the trailing «Сума». <c>ChiefJudge</c>,
/// <c>ChiefSecretary</c> and <c>Jury</c> are the signature-block roles, <c>Footer*</c> the page-footer
/// captions.
/// </remarks>
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
