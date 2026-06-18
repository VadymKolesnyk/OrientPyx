using OrientDesk.BusinessLogic.Enums;
using OrientDesk.BusinessLogic.Interfaces;
using OrientDesk.BusinessLogic.Models;

namespace OrientDesk.BusinessLogic.Disciplines;

/// <summary>
/// Shared base for the discipline strategies. Provides the common control-point counting and the
/// always-true <see cref="GroupColumn.TimeLimit"/> rule, so each concrete strategy only has to
/// declare the columns specific to it.
/// </summary>
public abstract class DisciplineStrategyBase : IDisciplineStrategy
{
    public abstract DisciplineType Type { get; }

    public string NameKey => "Discipline.Type." + Type;

    public virtual bool UsesControlPointPoints => false;

    /// <summary>
    /// Every discipline shows the (auto-computed) control count and a time limit; concrete strategies
    /// extend this for their own columns.
    /// </summary>
    public virtual bool UsesColumn(GroupColumn column)
        => column is GroupColumn.ControlCount or GroupColumn.TimeLimit;

    /// <summary>By default no participant column is discipline-specific; concrete strategies opt in.</summary>
    public virtual bool UsesParticipantColumn(ParticipantColumn column) => false;

    /// <summary>
    /// Counts control points in a free-text course order, skipping start/finish markers. A token is
    /// treated as a start/finish marker when it begins with 'S' or 'F' (case-insensitive) and is not
    /// purely numeric (e.g. "S1", "F", "Start"); everything else counts as a control point.
    /// </summary>
    public virtual int ControlCount(string courseOrder)
    {
        if (string.IsNullOrWhiteSpace(courseOrder))
            return 0;

        var count = 0;
        foreach (var token in courseOrder.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (IsStartOrFinish(token))
                continue;
            count++;
        }
        return count;
    }

    private static bool IsStartOrFinish(string token)
    {
        var first = token[0];
        if (first is not ('S' or 's' or 'F' or 'f'))
            return false;

        // "31" stays a control point; "S1"/"F"/"Start" are markers. Purely numeric tokens never
        // reach here (they don't start with S/F), so any non-numeric S*/F* token is a marker.
        return !ulong.TryParse(token, out _);
    }

    /// <summary>
    /// No finish evaluation by default — the score formats don't judge by order/finish-punch and will
    /// get their own rules later. Set-course overrides this.
    /// </summary>
    public virtual FinishStatusResult EvaluateFinish(FinishContext context) => FinishStatusResult.Of(FinishStatus.None);

    /// <summary>
    /// Default splits layout is the scored one (score / choice / rogaine): the controls the chip
    /// punched, in passage order, each with its point value, the time and a running total — followed by
    /// the allowed controls that were not visited (greyed, no time). Order is just the passage; there is
    /// no "right/wrong order" for a free-choice format. Set course overrides this with the ordered layout.
    /// </summary>
    public virtual SplitsView BuildSplits(SplitsContext context) => BuildScoredSplits(context);

    /// <summary>
    /// Shared scored-layout builder used by every free-choice discipline. A control is counted once even
    /// if punched twice (the first punch scores); only controls in the allowed set count toward points.
    /// </summary>
    protected static SplitsView BuildScoredSplits(SplitsContext context)
    {
        var allowed = new HashSet<string>(
            context.ExpectedControls.Select(c => c.Trim()), StringComparer.OrdinalIgnoreCase);

        var entries = new List<ScoreEntry>();
        var counted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var total = 0;

        // Visited controls in passage order. Skip foreign punches (not in the allowed set) and repeats.
        foreach (var punch in context.Punches)
        {
            var code = punch.ControlCode.Trim();
            if (code.Length == 0 || !allowed.Contains(code) || !counted.Add(code))
                continue;

            var points = context.PointsByCode.TryGetValue(code, out var p) ? p : 0;
            total += points;
            var elapsed = context.StartTime is { } s && punch.Time is { } t ? t - s : (TimeSpan?)null;
            entries.Add(new ScoreEntry(code, Visited: true, points, punch.Time, elapsed, total));
        }

        // Allowed controls that were never punched, in course order, greyed (no time, total unchanged).
        foreach (var expected in context.ExpectedControls)
        {
            var code = expected.Trim();
            if (code.Length == 0 || counted.Contains(code))
                continue;

            var points = context.PointsByCode.TryGetValue(code, out var p) ? p : 0;
            entries.Add(new ScoreEntry(code, Visited: false, points, PunchTime: null, Elapsed: null, total));
        }

        return new SplitsView
        {
            Layout = SplitsLayout.Scored,
            Entries = entries,
            TotalPoints = total,
            VisitedCount = counted.Count,
            ExpectedCount = allowed.Count
        };
    }

    // --- Shared leg geometry (used by the ordered set-course and rogaine layouts) ------------------

    /// <summary>The geographic coordinate of a control code, or a coordinate-less point when unknown.</summary>
    protected static GeoPoint ResolveCoord(SplitsContext context, string code) =>
        context.CoordsByCode.TryGetValue(code, out var p) ? p : default;

    /// <summary>The paper-map position of a control code, or a position-less point when unknown.</summary>
    protected static MapPoint ResolveMap(SplitsContext context, string code) =>
        context.MapByCode.TryGetValue(code, out var p) ? p : default;

    /// <summary>
    /// Straight-line distance (km) of the leg between two positions, via the shared course distance
    /// calculator. Prefers the paper-map source (map mm × scale, undistorted) when both endpoints are
    /// mapped and a scale is known; otherwise falls back to the geographic coordinates. Null when neither
    /// source covers both endpoints.
    /// </summary>
    protected static decimal? LegDistanceKm(
        ICourseDistanceCalculator distance, SplitsContext context,
        GeoPoint from, GeoPoint to, MapPoint fromMap, MapPoint toMap)
    {
        if (context.MapScale is > 0 && fromMap.HasCoordinates && toMap.HasCoordinates)
        {
            var mapKm = distance.TotalKilometresFromMap([fromMap, toMap], context.MapScale.Value);
            return mapKm > 0m ? mapKm : null;
        }

        if (!from.HasCoordinates || !to.HasCoordinates)
            return null;

        var km = distance.TotalKilometres([from, to]);
        return km > 0m ? km : null;
    }

    /// <summary>Pace in seconds per kilometre for one leg; null when the leg time or distance is missing/zero.</summary>
    protected static double? PaceSecondsPerKm(TimeSpan? leg, decimal? legKm)
    {
        if (leg is not { } t || t <= TimeSpan.Zero || legKm is not { } km || km <= 0m)
            return null;
        return t.TotalSeconds / (double)km;
    }
}
