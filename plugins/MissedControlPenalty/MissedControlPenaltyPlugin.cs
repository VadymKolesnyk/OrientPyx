using OrientPyx.Plugins.Sdk;

namespace MissedControlPenalty;

/// <summary>
/// «Штраф замість зняття»: a missed control costs a time penalty instead of a disqualification (Р-52…Р-58).
/// Two disciplines — on a set course and on a mixed course (Р-53).
/// </summary>
public sealed class MissedControlPenaltyPlugin : IOrientPyxPlugin
{
    public PluginInfo Info => new("missed-penalty", "Штраф замість зняття", "1.0",
        Author: "OrientPyx", Description: "Пропущений КП — штраф часу замість зняття.");

    public void Register(IPluginBuilder p)
    {
        p.AddDiscipline<SetCoursePenalty>();
        p.AddDiscipline<MixedPenalty>();
        p.AddStrings("uk-UA", Strings.Uk);
        p.AddStrings("en-US", Strings.En);
    }
}

public sealed class SetCoursePenalty : MissedPenaltyDiscipline
{
    public override string Id => "missed-penalty.set-course";

    public override string Name => "name.setCourse";

    public override BuiltinDiscipline BasedOn => BuiltinDiscipline.SetCourse;
}

public sealed class MixedPenalty : MissedPenaltyDiscipline
{
    public override string Id => "missed-penalty.mixed";

    public override string Name => "name.mixed";

    public override BuiltinDiscipline BasedOn => BuiltinDiscipline.Mixed;
}

public abstract class MissedPenaltyDiscipline : Discipline
{
    /// <summary>Р-58: no sports ranks for a result with forgiven controls.</summary>
    public override bool AwardsRanks => false;

    public override IEnumerable<SettingDef> Settings =>
    [
        Setting.Int("minutes", "setting.minutes", defaultValue: 5, min: 0, max: 120),
        Setting.Int("maxMissed", "setting.maxMissed", defaultValue: 3, min: 0, max: 50),
    ];

    public override IEnumerable<ResultColumnDef> Columns =>
    [
        Column.Number("missed", "column.missed", r => r.Get<int?>("missed")),
        Column.Time("penalty", "column.penalty", r => r.Get<TimeSpan?>("penalty")),
    ];

    public override FinishStatusResult EvaluateFinish(FinishContext ctx)
    {
        var r = base.EvaluateFinish(ctx);                    // the built-in check (Р-52)
        if (r.Status != FinishStatus.Mp
            || ctx.FinishTime is null                        // no finish — DNF (Р-54)
            || r.Missed.Count > ctx.Setting<int>("maxMissed"))
            return r;
        // The built-in check returns MP before it looks at the time limit (DNF > MP > OVT), so the OVT of a
        // forgiven MP is checked here — on the actual time (Р-57).
        return r with { Status = ctx.IsOverTime ? FinishStatus.Ovt : FinishStatus.Ok };   // Р-55
    }

    public override TimeSpan AdjustTime(AdjustContext ctx)
    {
        var missed = ctx.Row.Missed.Count;
        var penalty = TimeSpan.FromMinutes(ctx.Setting<int>("minutes")) * missed;
        ctx.Row.Set("missed", missed);
        ctx.Row.Set("penalty", missed > 0 ? penalty : null); // «—» when there is no penalty
        return ctx.Time + penalty;                           // Р-56
    }
}

internal static class Strings
{
    public static readonly IReadOnlyDictionary<string, string> Uk = new Dictionary<string, string>
    {
        ["name.setCourse"] = "Заданий напрямок зі штрафом за КП",
        ["name.mixed"] = "Змішаний зі штрафом за КП",
        ["setting.minutes"] = "Штраф за пропущений КП, хв",
        ["setting.maxMissed"] = "Найбільше пропущених КП без зняття",
        ["column.missed"] = "Пропущено КП",
        ["column.penalty"] = "Штраф",
    };

    public static readonly IReadOnlyDictionary<string, string> En = new Dictionary<string, string>
    {
        ["name.setCourse"] = "Set course with missed-control penalty",
        ["name.mixed"] = "Mixed course with missed-control penalty",
        ["setting.minutes"] = "Penalty per missed control, min",
        ["setting.maxMissed"] = "Most missed controls without disqualification",
        ["column.missed"] = "Missed controls",
        ["column.penalty"] = "Penalty",
    };
}
