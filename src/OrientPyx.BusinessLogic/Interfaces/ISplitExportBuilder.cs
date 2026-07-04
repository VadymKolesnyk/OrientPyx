using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Interfaces;

/// <summary>
/// Turns the raw <see cref="SplitExportData"/> gathered for a day, plus the header text and localized
/// labels, into a renderable <see cref="SplitExportDocument"/>: it formats each runner's result, orders the
/// rows (placed finishers first), and resolves the header. Layer-neutral (no UI, no document library) — the
/// rendering to HTML is the writer's job (<see cref="ISplitHtmlWriter"/>). Mirrors
/// <see cref="IResultProtocolBuilder"/>.
/// </summary>
public interface ISplitExportBuilder
{
    /// <summary>Builds the document for one day from the gathered data, the header text and the labels.</summary>
    SplitExportDocument Build(SplitExportData data, SplitExportHeader header, SplitExportLabels labels);
}

/// <summary>The header text for a split export (the caller folds in competition fallbacks before passing it).</summary>
public sealed record SplitExportHeader(
    string Title,
    string Subtitle,
    string CompetitionType,
    string Venue,
    string DateText);
