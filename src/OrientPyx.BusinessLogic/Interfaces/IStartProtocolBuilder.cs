using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Interfaces;

/// <summary>
/// Turns raw day start data (<see cref="StartProtocolData"/>) plus the user's <see cref="StartProtocolSettings"/>
/// and the localized <see cref="StartProtocolLabels"/> into a renderable <see cref="ResultProtocolDocument"/>
/// (the same document model the results protocol uses, so it shares the .docx writer and the live preview).
/// The <paramref name="kind"/> decides the structure: <see cref="StartProtocolKind.Regular"/> = one section
/// per group ordered by start time within the group; <see cref="StartProtocolKind.Judges"/> = one section per
/// start minute, members of that minute (across all groups) under it. Runners with no assigned start time
/// sort last (a trailing "no time" section in the judges' protocol; the end of each group in the regular one).
/// Layer-neutral — no UI, no document library — so it lives in BusinessLogic and is unit-testable.
/// </summary>
public interface IStartProtocolBuilder
{
    ResultProtocolDocument Build(
        StartProtocolData data,
        StartProtocolSettings settings,
        StartProtocolKind kind,
        StartProtocolLabels labels);
}

/// <summary>
/// Localized captions the start-protocol builder needs that don't come from competition data: the default
/// title (per kind), each column's header text, and the "no start time" section caption used in the judges'
/// protocol. Supplied by the Presentation layer from <c>ILocalizationService</c> so the builder stays
/// localization-free.
/// </summary>
public sealed record StartProtocolLabels(
    string DefaultTitle,
    IReadOnlyDictionary<StartProtocolColumn, string> ColumnHeaders,
    string NoStartTimeCaption,
    string CourseSetterLabel = "",
    string ChiefJudgeLabel = "",
    string ChiefSecretaryLabel = "",
    string JuryLabel = "",
    IReadOnlyDictionary<StartProtocolColumn, string>? ColumnHeadersShort = null,
    /// <summary>The program name printed in the page footer ("П/З: OrientPyx"). Blank ⇒ no footer.</summary>
    string FooterSoftwareName = "",
    /// <summary>Caption before the footer's generation timestamp ("Згенеровано").</summary>
    string FooterGeneratedLabel = "",
    /// <summary>Caption before the footer's page number ("Сторінка").</summary>
    string FooterPageLabel = "");
