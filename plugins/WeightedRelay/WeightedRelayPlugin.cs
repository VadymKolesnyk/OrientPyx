using System.Globalization;
using OrientPyx.Plugins.Sdk;
using OrientPyx.Plugins.Sdk.Toolkit;

namespace WeightedRelay;

/// <summary>
/// «Зважена естафета»: each leg time is multiplied by the runner's coefficient K (age × gender, Р-45); the team time
/// is Σ Tᵢ·Kᵢ. The coefficient table is one per competition (Р-50).
/// </summary>
public sealed class WeightedRelayPlugin : IOrientPyxPlugin
{
    public PluginInfo Info => new("weighted", "Зважена естафета", "1.6", MinApiVersion: 4,
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
        Column.Number("k", "column.k", r => r.Get<double?>("k"), format: Coefficients.Format,
            problem: r => r.Get<bool>(MissingKey)),                                     // Р-48: red when unknown
        // Online «Результат» of a leg already is T·K — a column of its own there would only repeat it.
        Column.Time("adj", "column.adj", r => r.Get<TimeSpan?>("adj")) with { Online = false },
    ];

    private const string MissingKey = "k.missing";

    public override TimeSpan AdjustTime(AdjustContext ctx)
    {
        // «Час» — the clean leg time T beside the weighted one, whether or not K is known.
        ctx.Row.Set("time", ctx.Time);

        if (Coefficients.Of(ctx.Setting<TableValue>("k"), ctx.Participant, ctx.Date) is not { } coefficient)
        {
            // Р-48: no birth year / gender (or no K for it) — the team gets no place; its time counts this leg with K = 1.
            ctx.Row.Set(MissingKey, true);
            ctx.Row.MarkUnranked(MissingKey);
            return ctx.Time;
        }

        // Р-49: T·K keeps its full precision, like every time; the host drops the fraction only when it shows it —
        // so with K = 1 «T·K» shows exactly the «Час».
        var adj = ctx.Time * coefficient;
        ctx.Row.Set("k", coefficient);
        ctx.Row.Set("adj", adj);
        return adj;
    }
}

/// <summary>
/// «Перевірка K»: the runners of the day's weighted relay teams whose coefficient cannot be found — no birth year, or
/// a gender other than «Ч»/«Ж» (Р-48: their team gets no place). One section per weighted group, a team row over its
/// runners with the K their leg time will be multiplied by (from the group's coefficient table, before anyone runs);
/// with «Лише проблемні» (default) only the teams with a problem.
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
        var date = ctx.Day?.Date ?? DateOnly.FromDateTime(DateTime.Today);
        var sections = new List<ReportSection>();
        foreach (var group in ctx.Day?.Groups ?? [])
        {
            if (!group.Discipline.StartsWith("weighted.", StringComparison.Ordinal))
                continue;

            var table = group.Setting<TableValue>("k");
            var rows = new List<ReportRow>();
            foreach (var team in group.Teams)
            {
                var legs = team.Legs.Select((runner, i) => (Leg: i + 1, Runner: runner, Problem: Problem(ctx, runner))).ToList();
                if (onlyProblems && legs.All(l => l.Problem is null))
                    continue;
                rows.Add(ReportRow.Caption(team.Number > 0 ? team.Number : null, team.Name, null, null, null, null));
                foreach (var (leg, runner, problem) in legs)
                    rows.Add(ReportRow.Sub(leg, runner?.Participant.FullName ?? ctx.Text("report.noRunner"),
                        runner?.Participant.BirthYear, runner?.Participant.Field("gender"),
                        runner is null ? null : Coefficients.Text(table, runner.Participant, date), problem));
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
                new ReportColumn("column.k"),
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

/// <summary>The default coefficient table (§14): age range × gender; age = the day's year − birth year, so «0–15» =
/// «15 і молодші», «80+» = «80 і старші».</summary>
internal static class Coefficients
{
    public static readonly IReadOnlyList<TableColumnDef> Columns =
    [
        TableColumnDef.Text("range", "table.age"),
        TableColumnDef.Number("Ч", "table.male"),
        TableColumnDef.Number("Ж", "table.female"),
    ];

    public static readonly TableValue Default = TableValue.FromRows(Columns,
        ["0–15", "0.950", "0.850"],
        ["16–17", "0.990", "0.870"],
        ["18–34", "1.000", "0.890"],
        ["35–39", "0.975", "0.860"],
        ["40–44", "0.920", "0.790"],
        ["45–49", "0.870", "0.755"],
        ["50–54", "0.830", "0.710"],
        ["55–59", "0.785", "0.670"],
        ["60–64", "0.745", "0.620"],
        ["65–69", "0.695", "0.575"],
        ["70–74", "0.640", "0.530"],
        ["75–79", "0.585", "0.465"],
        ["80+", "0.505", "0.395"]);

    /// <summary>How K is shown: three decimals, as the table gives it (0.975, 0.755).</summary>
    public const string Format = "0.000";

    /// <summary>The runner's K (Р-45…Р-47): age in the year of <paramref name="date"/> × gender; outside the table —
    /// the nearest row. Null without a birth year or gender, or when the table has no value for them (Р-48).</summary>
    public static double? Of(TableValue? table, ParticipantView participant, DateOnly date) =>
        participant.BirthYear is { } year && participant.Field("gender") is { } gender
            ? RangeTable.FromSetting(table).Lookup(Age.InYear(year, date), gender)
            : null;

    /// <summary><see cref="Of"/> as the «K» column shows it; null when K cannot be found.</summary>
    public static string? Text(TableValue? table, ParticipantView participant, DateOnly date) =>
        Of(table, participant, date)?.ToString(Format, CultureInfo.InvariantCulture);
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
