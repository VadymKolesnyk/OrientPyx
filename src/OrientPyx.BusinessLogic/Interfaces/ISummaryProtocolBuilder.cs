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

    /// <summary>
    /// Runs only the cross-day aggregation + ranking (no header/column formatting) and returns each group's ranked
    /// entries in printed order (placed members first by place, ties sharing a place; then поза конкурсом). Lets
    /// the winners printout reuse the exact «Підсумковий залік» ranking. The «Сума» text is formatted per the
    /// chosen mode (total points or total time).
    /// </summary>
    IReadOnlyList<SummaryRankedGroup> RankGroups(SummaryProtocolData data, SummaryProtocolSettings settings);
}
