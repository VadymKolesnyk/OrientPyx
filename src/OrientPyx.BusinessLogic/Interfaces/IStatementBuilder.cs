using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Interfaces;

/// <summary>
/// Turns raw participant data (<see cref="StatementData"/>) plus the user's <see cref="StatementSettings"/>,
/// the localized <see cref="StatementLabels"/> and the currently-applied filter summary into a renderable
/// <see cref="ResultProtocolDocument"/> (reusing the results-protocol document/writer/preview): resolves the
/// header text, maps each visible column to a formatted cell, sorts the whole list by chip (rental first, then
/// own, then by chip number), marks own-chip cells bold, and stamps the filter summary onto the document.
/// Layer-neutral — no UI, no document library — so it lives in BusinessLogic and is unit-testable.
/// </summary>
public interface IStatementBuilder
{
    ResultProtocolDocument Build(
        StatementData data, StatementSettings settings, StatementLabels labels, string filterSummary);
}

/// <summary>
/// Localized captions the statement builder needs that don't come from competition data: the default title
/// and each column's header text (full + optional short form). Supplied by the Presentation layer from
/// <c>ILocalizationService</c> so the builder stays localization-free.
/// </summary>
public sealed record StatementLabels(
    string DefaultTitle,
    IReadOnlyDictionary<StatementColumn, string> ColumnHeaders,
    IReadOnlyDictionary<StatementColumn, string>? ColumnHeadersShort = null,
    /// <summary>Header template for one day's column in the per-day «Старт» block, e.g. "Старт ({0})" — the
    /// builder fills {0} with each day's short label ("Д1"). When the block has a single day (day mode, or a
    /// one-day competition) the plain <see cref="ColumnHeaders"/>[Start] caption is used instead, so a one-day
    /// statement just reads "Старт".</summary>
    string StartDayHeaderTemplate = "",
    /// <summary>The program name printed in the page footer ("П/З: OrientPyx"). Blank ⇒ no footer.</summary>
    string FooterSoftwareName = "",
    /// <summary>Caption before the footer's generation timestamp ("Згенеровано").</summary>
    string FooterGeneratedLabel = "",
    /// <summary>Caption before the footer's page number ("Сторінка").</summary>
    string FooterPageLabel = "");
