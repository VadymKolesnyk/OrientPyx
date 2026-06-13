using OrientDesk.BusinessLogic.Enums;
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
}
