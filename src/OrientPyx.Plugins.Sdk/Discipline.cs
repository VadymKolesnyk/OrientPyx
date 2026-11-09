using System.ComponentModel;

namespace OrientPyx.Plugins.Sdk;

/// <summary>The built-in disciplines a plugin discipline can be based on (identifiers only — the implementations
/// live in the application).</summary>
public enum BuiltinDiscipline
{
    SetCourse,
    ScoreByCount,
    ScoreByTime,
    Rogaine,
    Mixed,
    Scatter,
    Relay
}

/// <summary>
/// The built-in behaviour a plugin discipline falls back to: <c>base.EvaluateFinish(ctx)</c> and the other
/// defaults run the <see cref="Discipline.BasedOn"/> discipline with the same contexts the application built.
/// Supplied by the host; plugins do not implement it.
/// </summary>
public interface IDisciplineHost
{
    FinishStatusResult EvaluateFinish(FinishContext context);

    SplitsView BuildSplits(SplitsContext context);

    DateTimeOffset? ResolveStart(StartContext context);

    TimeSpan AdjustTime(AdjustContext context);

    void Rank(RankingContext context);

    bool AwardsRanks { get; }
}

/// <summary>
/// A plugin competition type. Everything not overridden behaves like <see cref="BasedOn"/>; the format's structure
/// (team model, group columns, control-point points) always comes from <see cref="BasedOn"/> — a plugin changes the
/// rules, not the shape of the format.
/// </summary>
public abstract class Discipline
{
    private IDisciplineHost? _host;

    /// <summary>Stable id, conventionally <c>"&lt;pluginId&gt;.&lt;name&gt;"</c>; stored in the competition.</summary>
    public abstract string Id { get; }

    /// <summary>Display name — a key of the plugin's strings, or plain text.</summary>
    public abstract string Name { get; }

    /// <summary>The built-in discipline everything not overridden is inherited from.</summary>
    public virtual BuiltinDiscipline BasedOn => BuiltinDiscipline.SetCourse;

    /// <summary>Connects the discipline to the host's built-in implementation. Called by the host.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public void Attach(IDisciplineHost host) => _host = host;

    private IDisciplineHost Host =>
        _host ?? throw new InvalidOperationException($"Discipline '{Id}' is not attached to a host.");

    /// <summary>The finish status of one read-out (DNF / MP / OVT / OK …).</summary>
    public virtual FinishStatusResult EvaluateFinish(FinishContext ctx) => Host.EvaluateFinish(ctx);

    /// <summary>The splits view under the read-out log.</summary>
    public virtual SplitsView BuildSplits(SplitsContext ctx) => Host.BuildSplits(ctx);

    /// <summary>Where the runner's time starts.</summary>
    public virtual DateTimeOffset? ResolveStart(StartContext ctx) => Host.ResolveStart(ctx);

    /// <summary>Corrects an OK result's time before ranking (a penalty, a coefficient). The host rounds a changed
    /// value to a whole second.</summary>
    public virtual TimeSpan AdjustTime(AdjustContext ctx) => Host.AdjustTime(ctx);

    /// <summary>Places the group's finished competitors.</summary>
    public virtual void Rank(RankingContext ctx) => Host.Rank(ctx);

    /// <summary>Whether results may earn a sports rank («розряд»).</summary>
    public virtual bool AwardsRanks => Host.AwardsRanks;

    /// <summary>Extra result columns (values from <see cref="ResultRow.Set"/>).</summary>
    public virtual IEnumerable<ResultColumnDef> Columns => [];

    /// <summary>The discipline's settings, edited on the plugin's own page (menu «Плагіни» → the plugin).</summary>
    public virtual IEnumerable<SettingDef> Settings => [];

    /// <summary>Participant field keys the discipline needs (shown as columns on the roster).</summary>
    public virtual IEnumerable<string> RequiredFields => [];
}
