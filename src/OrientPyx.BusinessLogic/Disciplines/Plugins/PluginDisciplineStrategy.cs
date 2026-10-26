using OrientPyx.BusinessLogic.Enums;
using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Disciplines.Plugins;

/// <summary>
/// Adapts a plugin <see cref="Discipline"/> to <see cref="IDisciplineStrategy"/>. The format's structure (columns,
/// team model, variants, points…) comes from the built-in <see cref="Discipline.BasedOn"/> strategy; the rules
/// (finish, splits, start, time adjustment, ranking) come from the plugin. Every call into plugin code is guarded:
/// on an exception the built-in result is used, the error is logged and the user told once (Р-3).
/// </summary>
public sealed class PluginDisciplineStrategy : IDisciplineStrategy
{
    private readonly Discipline _discipline;
    private readonly IDisciplineStrategy _basedOn;
    private readonly PluginRuntimeErrors _errors;
    private readonly bool _overridesRank;

    public PluginDisciplineStrategy(
        Discipline discipline, string pluginId, IDisciplineStrategy basedOn, PluginRuntimeErrors errors)
    {
        _discipline = discipline;
        _basedOn = basedOn;
        _errors = errors;
        PluginId = pluginId;
        Id = new DisciplineId(discipline.Id);
        discipline.Attach(new DisciplineHost(basedOn));

        NameKey = Guard(nameof(Discipline.Name), () => PluginStrings.Key(pluginId, discipline.Name), Id.Value);
        Settings = Guard(nameof(Discipline.Settings), () => discipline.Settings.ToList(), []);
        ResultColumns = Guard(nameof(Discipline.Columns), () => discipline.Columns
            .Select(c => c with { Label = PluginStrings.Key(pluginId, c.Label) }).ToList(), []);
        RequiredFields = Guard(nameof(Discipline.RequiredFields), () => discipline.RequiredFields.ToList(), []);
        AwardsRanks = Guard(nameof(Discipline.AwardsRanks), () => discipline.AwardsRanks, basedOn.AwardsRanks);
        _overridesRank = discipline.GetType().GetMethod(nameof(Discipline.Rank), [typeof(RankingContext)])
            ?.DeclaringType != typeof(Discipline);
    }

    /// <summary>The wrapped plugin discipline.</summary>
    public Discipline Discipline => _discipline;

    /// <summary>The built-in strategy this one is based on.</summary>
    public IDisciplineStrategy BasedOn => _basedOn;

    public DisciplineId Id { get; }

    public string? PluginId { get; }

    public string NameKey { get; }

    public IReadOnlyList<SettingDef> Settings { get; }

    public IReadOnlyList<ResultColumnDef> ResultColumns { get; }

    public IReadOnlyList<string> RequiredFields { get; }

    public bool AwardsRanks { get; }

    public bool IsUnknown => false;

    public CompetitorModel Competitors => _basedOn.Competitors;

    public bool UsesCourseVariants => _basedOn.UsesCourseVariants;

    public bool UsesCoursePattern => _basedOn.UsesCoursePattern;

    public bool UsesControlPointPoints => _basedOn.UsesControlPointPoints;

    public bool ChecksStartControlClash => _basedOn.ChecksStartControlClash;

    public decimal? DefaultPenaltyPerMinute => _basedOn.DefaultPenaltyPerMinute;

    public bool UsesColumn(GroupColumn column) => _basedOn.UsesColumn(column);

    public bool UsesParticipantColumn(ParticipantColumn column) => _basedOn.UsesParticipantColumn(column);

    public int ControlCount(string courseOrder) => _basedOn.ControlCount(courseOrder);

    public FinishStatusResult EvaluateFinish(FinishContext context) =>
        Guard(nameof(Discipline.EvaluateFinish), () => _discipline.EvaluateFinish(context),
            () => _basedOn.EvaluateFinish(context));

    public SplitsView BuildSplits(SplitsContext context) =>
        Guard(nameof(Discipline.BuildSplits), () => _discipline.BuildSplits(context),
            () => _basedOn.BuildSplits(context));

    public DateTimeOffset? ResolveStart(StartContext context) =>
        Guard(nameof(Discipline.ResolveStart), () => _discipline.ResolveStart(context),
            () => _basedOn.ResolveStart(context));

    public TimeSpan AdjustTime(AdjustContext context) =>
        Guard(nameof(Discipline.AdjustTime), () => _discipline.AdjustTime(context), () => context.Time);

    public void Rank(GroupRankingContext context)
    {
        // A plugin that keeps the built-in ranking (or a team format, whose ranking is structural) runs the
        // built-in pass directly, on the host's own data.
        if (!_overridesRank || Competitors != CompetitorModel.Individual)
        {
            _basedOn.Rank(context);
            return;
        }

        var rows = context.Entries.Select(e =>
        {
            var result = context.Results[e.LinkId];
            return new ResultRow(new Dictionary<string, object?>(result.Extra))
            {
                Id = e.LinkId,
                Participant = context.Participants.GetValueOrDefault(e.LinkId),
                Status = result.Status,
                Time = result.ResultTime,
                AdjustedTime = result.AdjustedTime,
                Score = e.Scored ? e.Score : null,
                OutOfCompetition = result.OutOfCompetition,
                Missed = result.Missed
            };
        }).ToList();

        var sdk = new RankingContext
        {
            Group = context.GroupName,
            Competitors = rows,
            Finished = rows,
            Settings = context.DisciplineSettings,
            HostState = context
        };

        try
        {
            _discipline.Rank(sdk);
        }
        catch (Exception ex)
        {
            Report(nameof(Discipline.Rank), ex);
            _basedOn.Rank(context);
            return;
        }

        foreach (var row in rows)
            context.Results[row.Id] = context.Results[row.Id] with { Place = row.Place };
    }

    private T Guard<T>(string member, Func<T> call, Func<T> fallback)
    {
        try
        {
            return call();
        }
        catch (Exception ex)
        {
            Report(member, ex);
            return fallback();
        }
    }

    private T Guard<T>(string member, Func<T> call, T fallback) => Guard(member, call, () => fallback);

    private void Report(string member, Exception ex) =>
        _errors.Report(PluginId ?? string.Empty, Id, _basedOn.Id, member, ex);

    /// <summary>Runs <c>base.*</c> of a plugin discipline with the built-in strategy, on the contexts the host built.</summary>
    private sealed class DisciplineHost(IDisciplineStrategy basedOn) : IDisciplineHost
    {
        public FinishStatusResult EvaluateFinish(FinishContext context) => basedOn.EvaluateFinish(context);

        public SplitsView BuildSplits(SplitsContext context) => basedOn.BuildSplits(context);

        public DateTimeOffset? ResolveStart(StartContext context) => basedOn.ResolveStart(context);

        public TimeSpan AdjustTime(AdjustContext context) => basedOn.AdjustTime(context);

        public bool AwardsRanks => basedOn.AwardsRanks;

        public void Rank(RankingContext context)
        {
            if (context.HostState is GroupRankingContext host)
            {
                basedOn.Rank(host);
                foreach (var row in context.Competitors)
                    row.Place = host.Results.TryGetValue(row.Id, out var r) ? r.Place : null;
                return;
            }

            context.RankBy(Ranking.ByScoreThenTime<ResultRow>(r => r.Score ?? 0, r => r.AdjustedTime ?? r.Time));
        }
    }
}

/// <summary>How a plugin's own string keys are namespaced in the localization service.</summary>
public static class PluginStrings
{
    /// <summary>The application key of a plugin string: <c>"&lt;pluginId&gt;.&lt;key&gt;"</c>. Resolving it falls back
    /// to <paramref name="key"/> itself, so a plugin may write plain text instead of a key.</summary>
    public static string Key(string pluginId, string key) => pluginId + "." + key;
}
