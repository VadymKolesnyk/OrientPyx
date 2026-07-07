using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Interfaces;

/// <summary>
/// Builds the "winners" (призери) printout — the top prize places per group — from the same computed results
/// the protocols use. Two sources: a single competition day (<see cref="BuildForDay"/>, from the results
/// protocol's <see cref="ResultProtocolData"/>) and the multi-day summary (<see cref="BuildForSummary"/>, which
/// reuses the summary protocol's cross-day ranking so the winners match the «Підсумковий залік»). Shared (tied)
/// places are kept as multiple runners under one place entry, so the renderer can print "2 третіх" and both
/// names. Layer-neutral (BusinessLogic) — no printer/UI/document library refs.
/// </summary>
public interface IWinnersPrintBuilder
{
    /// <summary>
    /// Builds the winners printout for one day's results. <paramref name="topPlaces"/> is how many prize places
    /// to include per group (e.g. 3 for a podium); ties at the cut-off are kept whole (everyone sharing the last
    /// included place is printed). A team (rogaine) group ranks by team place; the winners are the teams.
    /// </summary>
    WinnersPrintDocument BuildForDay(
        ResultProtocolData data, WinnersPrintHeader header, WinnersPrintLabels labels, int topPlaces);

    /// <summary>
    /// Builds the winners printout for the multi-day summary, using the summary protocol's own aggregation and
    /// ranking (the chosen mode, counted days, priority day, require-all-days) so the printed winners match the
    /// «Підсумковий залік». Same <paramref name="topPlaces"/> semantics as <see cref="BuildForDay"/>.
    /// </summary>
    WinnersPrintDocument BuildForSummary(
        SummaryProtocolData data, SummaryProtocolSettings settings,
        WinnersPrintHeader header, WinnersPrintLabels labels, int topPlaces);
}

/// <summary>The resolved header text for the winners printout (the caller has already folded in the
/// competition/day fallbacks). Values-only, since the builder is layer-neutral.</summary>
public sealed record WinnersPrintHeader(string CompetitionName, string Title, string DateText);

/// <summary>
/// Localized heading builders the winners printout needs. The single-place heading ("1 місце") and the
/// shared-place phrasing ("2 третіх місця") come from the Presentation layer so the builder stays
/// localization-free; the builder resolves each place's heading and bakes it into the document.
/// </summary>
public sealed record WinnersPrintLabels(
    /// <summary>Heading for a place held by exactly one runner; receives the 1-based place number (e.g. 1 → "1 місце").</summary>
    Func<int, string> PlaceHeading,
    /// <summary>Heading for a place shared by several runners (a tie); receives the count and the place number
    /// (e.g. (2, 3) → "2 третіх місця").</summary>
    Func<int, int, string> SharedPlaceHeading);
