using OrientPyx.Plugins.Sdk;
using OrientPyx.Plugins.Sdk.Toolkit;

namespace WeightedRelay;

/// <summary>
/// «Зважена естафета»: each leg time is multiplied by the runner's coefficient K (age × gender, Р-45); the team time
/// is Σ Tᵢ·Kᵢ. The coefficient table is one per competition (Р-50).
/// </summary>
public sealed class WeightedRelayPlugin : IOrientPyxPlugin
{
    public PluginInfo Info => new("weighted", "Зважена естафета", "1.2", MinApiVersion: 2,
        Author: "OrientPyx", Description: "Час етапу × коефіцієнт за віком і статтю; час команди = Σ T·K.");

    public void Register(IPluginBuilder p)
    {
        p.AddParticipantField("gender", "Стать", FieldKind.Choice("Ч", "Ж"), Gender.Import);   // Р-51, Р-59
        p.AddDiscipline<WeightedRelayDiscipline>();
        p.AddReport<CheckReport>();
        p.AddStrings("uk-UA", Strings.Uk);
        p.AddStrings("en-US", Strings.En);
    }
}

/// <summary>«Стать»: the two options and how an import reads them (Р-59).</summary>
internal static class Gender
{
    public const string Male = "Ч";
    public const string Female = "Ж";

    /// <summary>UOF <c>&lt;Pol&gt;</c> (as the ФСОУ registration export names it): 1 = чоловік, 0 = жінка; IOF <c>&lt;Person sex="M|F"&gt;</c>; a CSV column «Стать» /
    /// «Gender» with any common spelling.</summary>
    public static readonly FieldImport Import = FieldImport.From("Pol", "Стать", "sex", "Gender", "Пол")
        .Value("1", Male).Value("0", Female)
        .Value("M", Male).Value("F", Female)
        .Value("male", Male).Value("female", Female)
        .Value("чол", Male).Value("жін", Female)
        .Value("чоловік", Male).Value("жінка", Female);

    public static bool IsKnown(string? value) => value is Male or Female;
}

/// <summary>The relay: legs, handoff, restarts and leg courses are the built-in relay's.</summary>
public sealed class WeightedRelayDiscipline : Discipline
{
    public override string Id => "weighted.relay";

    public override string Name => "name.relay";

    public override BuiltinDiscipline BasedOn => BuiltinDiscipline.Relay;

    /// <summary>Р-7: no sports ranks for a weighted result.</summary>
    public override bool AwardsRanks => false;

    public override IEnumerable<string> RequiredFields => ["gender"];

    public override IEnumerable<SettingDef> Settings =>
        [Setting.Table("k", "setting.k", Coefficients.Columns, Coefficients.Default, scope: SettingScope.Competition)];   // Р-50

    public override IEnumerable<ResultColumnDef> Columns =>
    [
        Column.Time("time", "column.time", r => r.Get<TimeSpan?>("time")),             // the leg's own time T
        Column.Number("k", "column.k", r => r.Get<double?>("k"), format: "0.00",
            problem: r => r.Get<bool>(MissingKey)),                                     // Р-48: red when unknown
        Column.Time("adj", "column.adj", r => r.Get<TimeSpan?>("adj")),
    ];

    private const string MissingKey = "k.missing";

    public override TimeSpan AdjustTime(AdjustContext ctx)
    {
        // «Час» — the clean leg time T beside the weighted one, whether or not K is known.
        ctx.Row.Set("time", ctx.Time);

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

/// <summary>
/// «Перевірка K»: the runners of the day's weighted relay teams whose coefficient cannot be found — no birth year, or
/// a gender other than «Ч»/«Ж» (Р-48: their team gets no place). One section per weighted group, a team row over its
/// runners; with «Лише проблемні» (default) only the teams with a problem.
/// </summary>
public sealed class CheckReport : Report
{
    public override string Id => "weighted.check";

    public override string Name => "report.check";

    public override string Description => "report.check.description";

    public override IEnumerable<SettingDef> Settings =>
        [Setting.Bool("onlyProblems", "report.onlyProblems", defaultValue: true, scope: SettingScope.Competition)];

    public override ReportDocument Build(ReportContext ctx)
    {
        var onlyProblems = ctx.Setting<bool>("onlyProblems");
        var sections = new List<ReportSection>();
        foreach (var group in ctx.Day?.Groups ?? [])
        {
            if (!group.Discipline.StartsWith("weighted.", StringComparison.Ordinal))
                continue;

            var rows = new List<ReportRow>();
            foreach (var team in group.Teams)
            {
                var legs = team.Legs.Select((runner, i) => (Leg: i + 1, Runner: runner, Problem: Problem(ctx, runner))).ToList();
                if (onlyProblems && legs.All(l => l.Problem is null))
                    continue;
                rows.Add(ReportRow.Caption(team.Number > 0 ? team.Number : null, team.Name, null, null, null));
                foreach (var (leg, runner, problem) in legs)
                    rows.Add(ReportRow.Sub(leg, runner?.Participant.FullName ?? ctx.Text("report.noRunner"),
                        runner?.Participant.BirthYear, runner?.Participant.Field("gender"), problem));
            }
            if (rows.Count > 0)
                sections.Add(new ReportSection { Title = group.Name, Rows = rows });
        }

        return new ReportDocument
        {
            Subtitle = sections.Count == 0 ? ctx.Text("report.allGood") : string.Empty,
            Officials = false,
            Columns =
            [
                new ReportColumn("report.col.number"),
                new ReportColumn("report.col.name") { Wrap = true },
                new ReportColumn("report.col.year"),
                new ReportColumn("report.col.gender"),
                new ReportColumn("report.col.problem") { Wrap = true },
            ],
            Sections = sections,
        };
    }

    // Why the runner's K cannot be found (Р-48); null when it can. A leg nobody runs is a problem too.
    private static string? Problem(ReportContext ctx, ReportResult? runner)
    {
        if (runner is null)
            return ctx.Text("report.noRunner");
        var gender = runner.Participant.Field("gender");
        var problems = new List<string>();
        if (runner.Participant.BirthYear is null)
            problems.Add(ctx.Text("report.noYear"));
        if (gender is null)
            problems.Add(ctx.Text("report.noGender"));
        else if (!Gender.IsKnown(gender))
            problems.Add(string.Format(ctx.Text("report.badGender"), gender));
        return problems.Count == 0 ? null : string.Join("; ", problems);
    }
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
        ["setting.k"] = "Коефіцієнти K (вік × стать)",
        ["table.age"] = "Вік",
        ["table.male"] = "K (Ч)",
        ["table.female"] = "K (Ж)",
        ["column.time"] = "Час",
        ["column.k"] = "K",
        ["column.adj"] = "T·K",
        ["k.missing"] = "K не визначено",
        ["report.check"] = "Перевірка K (стать і рік народження)",
        ["report.check.description"] = "Учасники команд зваженої естафети вибраного дня, для яких K не визначиться: команда буде без місця.",
        ["report.onlyProblems"] = "Лише команди з проблемами",
        ["report.allGood"] = "У всіх учасників є рік народження і стать «Ч» або «Ж».",
        ["report.col.number"] = "№",
        ["report.col.name"] = "Команда / учасник",
        ["report.col.year"] = "Рік",
        ["report.col.gender"] = "Стать",
        ["report.col.problem"] = "Проблема",
        ["report.noRunner"] = "етап нікому не призначено",
        ["report.noYear"] = "немає року народження",
        ["report.noGender"] = "не вказано стать",
        ["report.badGender"] = "стать «{0}» — має бути «Ч» або «Ж»",
    };

    public static readonly IReadOnlyDictionary<string, string> En = new Dictionary<string, string>
    {
        ["name.relay"] = "Weighted relay (T·K)",
        ["setting.k"] = "Coefficients K (age × gender)",
        ["table.age"] = "Age",
        ["table.male"] = "K (M)",
        ["table.female"] = "K (F)",
        ["column.time"] = "Time",
        ["column.k"] = "K",
        ["column.adj"] = "T·K",
        ["k.missing"] = "K not determined",
        ["report.check"] = "K check (gender and birth year)",
        ["report.check.description"] = "Runners of the picked day's weighted relay teams whose K cannot be found: their team will get no place.",
        ["report.onlyProblems"] = "Only teams with a problem",
        ["report.allGood"] = "Every runner has a birth year and the gender «Ч» or «Ж».",
        ["report.col.number"] = "No.",
        ["report.col.name"] = "Team / runner",
        ["report.col.year"] = "Year",
        ["report.col.gender"] = "Gender",
        ["report.col.problem"] = "Problem",
        ["report.noRunner"] = "no runner on the leg",
        ["report.noYear"] = "no birth year",
        ["report.noGender"] = "no gender",
        ["report.badGender"] = "gender «{0}» — must be «Ч» or «Ж»",
    };
}
