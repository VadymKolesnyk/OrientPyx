using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Interfaces;

/// <summary>
/// Abstraction over the shared application database (settings + last-session pointer).
/// Implemented in DataAccess.
/// </summary>
public interface IAppStore
{
    /// <summary>Default events path resolved by the infrastructure (./events).</summary>
    AppPaths GetDefaultPaths();

    /// <summary>Returns configured paths, or null if never set (caller applies defaults).</summary>
    Task<AppPaths?> GetPathsAsync(CancellationToken cancellationToken = default);

    Task SavePathsAsync(AppPaths paths, CancellationToken cancellationToken = default);

    /// <summary>Returns the stored UI font scale, or null if never set (caller applies default 1.0).</summary>
    Task<double?> GetFontScaleAsync(CancellationToken cancellationToken = default);

    Task SaveFontScaleAsync(double fontScale, CancellationToken cancellationToken = default);

    /// <summary>Returns the stored split-printout printer name + roll width, or null if never set.</summary>
    Task<(string PrinterName, int WidthMm)?> GetPrintSettingsAsync(CancellationToken cancellationToken = default);

    Task SavePrintSettingsAsync(string printerName, int widthMm, CancellationToken cancellationToken = default);

    /// <summary>Returns the stored results-protocol settings JSON, or null/blank when never saved.</summary>
    Task<string?> GetResultProtocolJsonAsync(CancellationToken cancellationToken = default);

    Task SaveResultProtocolJsonAsync(string json, CancellationToken cancellationToken = default);

    /// <summary>Returns the app-level default start-protocol settings JSON for the given kind, or null/blank
    /// when never saved.</summary>
    Task<string?> GetStartProtocolJsonAsync(StartProtocolKind kind, CancellationToken cancellationToken = default);

    Task SaveStartProtocolJsonAsync(StartProtocolKind kind, string json, CancellationToken cancellationToken = default);

    /// <summary>Returns the rank-validity conditions (min participants, min distinct regions per group, and how
    /// many top-ranked members count toward the group's course rank), or null when never set (caller applies
    /// the defaults).</summary>
    Task<(int MinParticipants, int MinRegions, int CountForRank)?> GetRankConditionsAsync(CancellationToken cancellationToken = default);

    Task SaveRankConditionsAsync(int minParticipants, int minRegions, int countForRank, CancellationToken cancellationToken = default);

    /// <summary>Returns the app-level online live-results connection settings (Supabase URL, service-role
    /// key, public frontend base URL, publish interval), applying defaults when never saved.</summary>
    Task<OnlineApiSettings> GetOnlineApiSettingsAsync(CancellationToken cancellationToken = default);

    Task SaveOnlineApiSettingsAsync(OnlineApiSettings settings, CancellationToken cancellationToken = default);

    /// <summary>Returns the stored readout format (0 = SPORTident, 1 = Sport Time), or null when never set
    /// (caller applies the default, SPORTident).</summary>
    Task<int?> GetReadoutTypeAsync(CancellationToken cancellationToken = default);

    Task SaveReadoutTypeAsync(int readoutType, CancellationToken cancellationToken = default);

    /// <summary>Returns the stored UI language culture name (e.g. "uk-UA"), or null/blank when never set
    /// (caller applies the default).</summary>
    Task<string?> GetLanguageAsync(CancellationToken cancellationToken = default);

    Task SaveLanguageAsync(string language, CancellationToken cancellationToken = default);

    /// <summary>Returns the last opened competition identifier + day number, if any.</summary>
    Task<(string? Identifier, int? DayNumber)> GetLastSessionAsync(CancellationToken cancellationToken = default);

    Task SaveLastSessionAsync(string? identifier, int? dayNumber, CancellationToken cancellationToken = default);

    // ── Sports ranks (application-level, shared across competitions) ────────────────────────────────

    /// <summary>Seeds the given ranks only when the ranks table is empty (first run). A no-op otherwise.</summary>
    Task SeedRanksIfEmptyAsync(IReadOnlyList<SportRank> ranks, CancellationToken cancellationToken = default);

    /// <summary>Loads all ranks, ordered by their display order then name.</summary>
    Task<IReadOnlyList<SportRank>> GetRanksAsync(CancellationToken cancellationToken = default);

    /// <summary>Appends a new, blank rank after the last one and returns it.</summary>
    Task<SportRank> AddRankAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves an edited rank (name, points). A change to a name already used by another rank is ignored
    /// (the previous name is kept), keeping names unique.
    /// </summary>
    Task UpdateRankAsync(SportRank rank, CancellationToken cancellationToken = default);

    /// <summary>Removes a rank. Participants keep their stored rank text; it just stops matching a known rank.</summary>
    Task DeleteRankAsync(Guid rankId, CancellationToken cancellationToken = default);

    // ── Points rules (application-level, shared across competitions) ─────────────────────────────────

    /// <summary>Seeds the given points rules only when the rules table is empty (first run). A no-op otherwise.</summary>
    Task SeedPointsRulesIfEmptyAsync(IReadOnlyList<PointsRule> rules, CancellationToken cancellationToken = default);

    /// <summary>Loads all points rules, ordered by their display order then name.</summary>
    Task<IReadOnlyList<PointsRule>> GetPointsRulesAsync(CancellationToken cancellationToken = default);

    /// <summary>Appends a new, blank points rule of the given kind after the last one and returns it.</summary>
    Task<PointsRule> AddPointsRuleAsync(PointsRuleKind kind, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves an edited points rule (name, table/formula). A change to a name already used by another rule
    /// is ignored (the previous name is kept), keeping names unique.
    /// </summary>
    Task UpdatePointsRuleAsync(PointsRule rule, CancellationToken cancellationToken = default);

    /// <summary>Removes a points rule.</summary>
    Task DeletePointsRuleAsync(Guid ruleId, CancellationToken cancellationToken = default);

    // ── Rank qualification table (application-level, shared across competitions) ──────────────────────

    /// <summary>Seeds the given qualification rows only when the table is empty (first run). A no-op otherwise.</summary>
    Task SeedRankQualificationIfEmptyAsync(IReadOnlyList<RankQualificationRow> rows, CancellationToken cancellationToken = default);

    /// <summary>Loads all qualification rows, ordered by display order then rank (high to low).</summary>
    Task<IReadOnlyList<RankQualificationRow>> GetRankQualificationAsync(CancellationToken cancellationToken = default);

    /// <summary>Appends a new, blank qualification row after the last one and returns it.</summary>
    Task<RankQualificationRow> AddRankQualificationRowAsync(CancellationToken cancellationToken = default);

    /// <summary>Saves an edited qualification row (rank threshold + cells).</summary>
    Task UpdateRankQualificationRowAsync(RankQualificationRow row, CancellationToken cancellationToken = default);

    /// <summary>Removes a qualification row.</summary>
    Task DeleteRankQualificationRowAsync(Guid rowId, CancellationToken cancellationToken = default);
}
