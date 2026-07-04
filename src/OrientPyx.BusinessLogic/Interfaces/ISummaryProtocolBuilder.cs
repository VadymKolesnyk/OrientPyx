using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Interfaces;

/// <summary>
/// Builds a renderable <see cref="SummaryProtocolDocument"/> (the multi-day «Підсумковий залік») from the
/// gathered cross-day data, the user's settings, and localized labels. Owns the aggregation and ranking
/// rules (sum of points vs sum of time, the tie-break priority day, the require-all-days option) and the
/// per-cell formatting; the renderer only lays the document out. Layer-neutral (BusinessLogic).
/// </summary>
public interface ISummaryProtocolBuilder
{
    SummaryProtocolDocument Build(SummaryProtocolData data, SummaryProtocolSettings settings, SummaryProtocolLabels labels);
}
