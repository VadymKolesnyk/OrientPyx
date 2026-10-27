using OrientPyx.Plugins.Sdk;
using OrientPyx.Plugins.Sdk.Toolkit;

namespace WeightedRelay;

/// <summary>
/// «Зважена естафета»: each leg time is multiplied by the runner's coefficient K (age × gender, Р-45); the team time
/// is Σ Tᵢ·Kᵢ. Also an individual variant placed by T·K (Р-51). The coefficient table is one per competition (Р-50).
/// </summary>
public sealed class WeightedRelayPlugin : IOrientPyxPlugin
{
    public PluginInfo Info => new("weighted", "Зважена естафета", "1.0",
        Author: "OrientPyx", Description: "Час етапу × коефіцієнт за віком і статтю; час команди = Σ T·K.");

    public void Register(IPluginBuilder p)
    {
        p.AddParticipantField("gender", "Стать", FieldKind.Choice(                    // Р-51
            new ChoiceOption("Ч", "M", "Чол", "Чоловіча"),
            new ChoiceOption("Ж", "F", "Жін", "Жіноча")));
        p.AddDiscipline<WeightedRelayDiscipline>();
        p.AddDiscipline<WeightedSetCourseDiscipline>();
        p.AddStrings("uk-UA", Strings.Uk);
        p.AddStrings("en-US", Strings.En);
    }
}

/// <summary>The relay: legs, handoff, restarts and leg courses are the built-in relay's.</summary>
public sealed class WeightedRelayDiscipline : WeightedDiscipline
{
    public override string Id => "weighted.relay";

    public override string Name => "name.relay";

    public override BuiltinDiscipline BasedOn => BuiltinDiscipline.Relay;
}

/// <summary>The individual variant: a set course, placed by T·K.</summary>
public sealed class WeightedSetCourseDiscipline : WeightedDiscipline
{
    public override string Id => "weighted.set-course";

    public override string Name => "name.setCourse";

    public override BuiltinDiscipline BasedOn => BuiltinDiscipline.SetCourse;
}

public abstract class WeightedDiscipline : Discipline
{
    /// <summary>Р-7: no sports ranks for a weighted result.</summary>
    public override bool AwardsRanks => false;

    public override IEnumerable<string> RequiredFields => ["gender"];

    public override IEnumerable<SettingDef> Settings =>
        [Setting.Table("k", "setting.k", Coefficients.Columns, Coefficients.Default, scope: SettingScope.Competition)];   // Р-50

    public override IEnumerable<ResultColumnDef> Columns =>
    [
        Column.Number("k", "column.k", r => r.Get<double?>("k"), format: "0.00",
            problem: r => r.Get<bool>(MissingKey)),                                     // Р-48: red when unknown
        Column.Time("adj", "column.adj", r => r.Get<TimeSpan?>("adj")),
    ];

    private const string MissingKey = "k.missing";

    public override TimeSpan AdjustTime(AdjustContext ctx)
    {
        double? k = null;
        if (ctx.Participant.BirthYear is { } year && ctx.Participant.Field("gender") is { } gender)
        {
            var table = RangeTable.FromSetting(ctx.Setting<TableValue>("k"));            // toolkit
            k = table.Lookup(Age.InYear(year, ctx.Date), gender);                       // outside the table — nearest row (Р-47)
        }

        if (k is not { } coefficient)
        {
            // Р-48: no birth year / gender (or no K for it) — the team gets no place; its time counts this leg with K = 1.
            ctx.Row.Set(MissingKey, true);
            ctx.Row.MarkUnranked(MissingKey);
            return ctx.Time;
        }

        // Р-49: every leg's T·K is rounded to a second — even with K = 1.00, where T·K equals T and the host
        // (Р-6) would otherwise keep the unrounded time; so the «T·K» column always sums to the team time.
        var adj = RoundToSecond(ctx.Time * coefficient);
        ctx.Row.Set("k", coefficient);
        ctx.Row.Set("adj", adj);
        return adj;
    }

    // The value shown in «T·K» — the same rounding the host applies to the ranked time (Р-49).
    private static TimeSpan RoundToSecond(TimeSpan time) =>
        TimeSpan.FromSeconds(Math.Round(time.TotalSeconds, MidpointRounding.AwayFromZero));
}

/// <summary>The default coefficient table (§14): age range × gender.</summary>
internal static class Coefficients
{
    public static readonly IReadOnlyList<TableColumnDef> Columns =
    [
        TableColumnDef.Text("range", "table.age"),
        TableColumnDef.Number("Ч", "table.male"),
        TableColumnDef.Number("Ж", "table.female"),
    ];

    public static readonly TableValue Default = TableValue.FromRows(Columns,
        ["8–10", "0.80", "0.78"],
        ["11–12", "0.86", "0.82"],
        ["13–14", "0.92", "0.84"],
        ["15–17", "0.97", "0.87"],
        ["18–34", "1.00", "0.89"],
        ["35–39", "0.98", "0.87"],
        ["40–44", "0.95", "0.84"],
        ["45–49", "0.91", "0.80"],
        ["50–54", "0.88", "0.76"],
        ["55–59", "0.84", "0.72"],
        ["60–64", "0.80", "0.68"],
        ["65–69", "0.76", "0.63"],
        ["70+", "0.71", "0.58"]);
}

internal static class Strings
{
    public static readonly IReadOnlyDictionary<string, string> Uk = new Dictionary<string, string>
    {
        ["name.relay"] = "Зважена естафета (T·K)",
        ["name.setCourse"] = "Заданий напрямок, час × K",
        ["setting.k"] = "Коефіцієнти K (вік × стать)",
        ["table.age"] = "Вік",
        ["table.male"] = "K (Ч)",
        ["table.female"] = "K (Ж)",
        ["column.k"] = "K",
        ["column.adj"] = "T·K",
        ["k.missing"] = "K не визначено",
    };

    public static readonly IReadOnlyDictionary<string, string> En = new Dictionary<string, string>
    {
        ["name.relay"] = "Weighted relay (T·K)",
        ["name.setCourse"] = "Set course, time × K",
        ["setting.k"] = "Coefficients K (age × gender)",
        ["table.age"] = "Age",
        ["table.male"] = "K (M)",
        ["table.female"] = "K (F)",
        ["column.k"] = "K",
        ["column.adj"] = "T·K",
        ["k.missing"] = "K not determined",
    };
}
