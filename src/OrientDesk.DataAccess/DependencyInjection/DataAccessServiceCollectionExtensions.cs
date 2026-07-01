using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrientDesk.BusinessLogic.Entities;
using OrientDesk.BusinessLogic.Interfaces;
using OrientDesk.BusinessLogic.Models;
using OrientDesk.DataAccess.Documents;
using OrientDesk.DataAccess.FileSystem;
using OrientDesk.DataAccess.Persistence;
using OrientDesk.DataAccess.Printing;

namespace OrientDesk.DataAccess.DependencyInjection;

public static class DataAccessServiceCollectionExtensions
{
    /// <summary>
    /// Registers the shared app database (./data/app.db), the per-event store/factory,
    /// and the events-folder scanner.
    /// </summary>
    public static IServiceCollection AddOrientDeskDataAccess(this IServiceCollection services)
    {
        services.AddDbContextFactory<AppDbContext>(options =>
            options.UseSqlite(AppDatabasePaths.GetAppConnectionString()));

        services.AddSingleton<IAppStore, AppStore>();

        // Per-launch diagnostic log under ./events/logs (actions + exceptions).
        services.AddSingleton<IActivityLog, FileActivityLog>();

        // EventStore opens per-competition databases on demand; it holds no shared state.
        services.AddSingleton<IEventStore, EventStore>();
        services.AddSingleton<IEventFolderScanner, EventFolderScanner>();

        // Reads .xlsx workbooks for the participant import (column mapping reuses the CSV path).
        services.AddSingleton<ISpreadsheetParser, XlsxParser>();

        // Writes .xlsx workbooks for the participants-table export (the CSV writer lives in BusinessLogic).
        services.AddSingleton<ITabularWriter, XlsxTabularWriter>();

        // Split printouts to an installed system printer (GDI; Windows-only at runtime).
        services.AddSingleton<ISplitPrintService, SplitPrintService>();

        // Renders a results protocol to a Word (.docx) document (Open XML; the builder is in BusinessLogic).
        services.AddSingleton<IResultProtocolWriter, DocxResultProtocolWriter>();

        // Renders the multi-day summary protocol (two-tier banded header) to a Word (.docx) document.
        services.AddSingleton<ISummaryProtocolWriter, DocxSummaryProtocolWriter>();

        // Renders a day's splits to a UTF-8 HTML document (the builder is in BusinessLogic).
        services.AddSingleton<ISplitHtmlWriter, HtmlSplitWriter>();

        // Renders an on-screen results monitor page (self-contained, auto-refresh + auto-scroll HTML).
        services.AddSingleton<IMonitorHtmlWriter, HtmlMonitorWriter>();

        // Publishes live results to Supabase (PostgREST). Transient: each running publish session needs its
        // own instance because the publisher remembers which metadata it has already uploaded.
        services.AddTransient<IResultPublisher, Online.SupabaseResultPublisher>();

        return services;
    }

    /// <summary>Ensures the shared app database exists and is up to date. Event databases are migrated on demand.</summary>
    public static void InitializeOrientDeskDatabase(this IServiceProvider services)
    {
        var factory = services.GetRequiredService<IDbContextFactory<AppDbContext>>();
        using var appDb = factory.CreateDbContext();
        // Applies all pending migrations, creating the database on first run.
        appDb.Database.Migrate();

        // Seed the default sports ranks once (first run, or first run after the ranks table is added).
        var store = services.GetRequiredService<IAppStore>();
        store.SeedRanksIfEmptyAsync(DefaultRanks).GetAwaiter().GetResult();

        // Seed the default points rules once (the nine placement tables + the time-ratio formula).
        store.SeedPointsRulesIfEmptyAsync(DefaultPointsRules).GetAwaiter().GetResult();

        // Seed the default rank qualification table once (Додаток 89, кваліфікаційна таблиця).
        store.SeedRankQualificationIfEmptyAsync(DefaultRankQualification).GetAwaiter().GetResult();
    }

    /// <summary>
    /// The canonical Ukrainian-orienteering rank set seeded on first run (МСМК → б/р ю), each with its
    /// default points. Editable afterwards on the Ranks page; this is only the starting list.
    /// </summary>
    private static readonly IReadOnlyList<SportRank> DefaultRanks =
    [
        new() { Name = "МСМК",  Points = 150, Order = 0 },
        new() { Name = "ЗМС",   Points = 150, Order = 1 },
        new() { Name = "МСУ",   Points = 100, Order = 2 },
        new() { Name = "КМСУ",  Points = 30,  Order = 3 },
        new() { Name = "I",     Points = 10,  Order = 4 },
        new() { Name = "II",    Points = 3,   Order = 5 },
        new() { Name = "III",   Points = 1,   Order = 6 },
        new() { Name = "I-ю",   Points = 3,   Order = 7 },
        new() { Name = "II-ю",  Points = 1,   Order = 8 },
        new() { Name = "III-ю", Points = 0.5, Order = 9 },
        new() { Name = "б/р",   Points = 0.5, Order = 10 },
        new() { Name = "б/р ю", Points = 0.3, Order = 11 },
    ];

    private static PointsRule Table(int order, string name, params int[] places) => new()
    {
        Name = name,
        Kind = PointsRuleKind.Table,
        TableJson = PointsTable.Serialize(places.Select(p => (decimal)p)),
        Order = order,
    };

    /// <summary>
    /// The default points rules seeded on first run: a time-ratio formula plus the nine canonical
    /// placement tables (Таб.1–Таб.9). Each table lists 1st-place points first. Editable afterwards on
    /// the Points page; this is only the starting list.
    /// </summary>
    private static readonly IReadOnlyList<PointsRule> DefaultPointsRules =
    [
        new()
        {
            Name = "100*(2-T_у/T_л)",
            Kind = PointsRuleKind.Formula,
            Formula = "100*(2 - T_у/T_л)",
            Order = 0,
        },
        Table(1, "45 42 40 38 36…",
            45, 42, 40, 38, 36, 35, 34, 33, 32, 31, 30, 29, 28, 27, 26,
            25, 24, 23, 22, 21, 20, 19, 18, 17, 16, 15, 14, 13, 12, 11,
            10, 9, 8, 7, 6, 5, 4, 3, 2, 1),
        Table(2, "35 32 30 28 26…",
            35, 32, 30, 28, 26, 25, 24, 23, 22, 21, 20, 19, 18, 17, 16,
            15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1),
        Table(3, "25 22 20 18 16…",
            25, 22, 20, 18, 16, 15, 14, 13, 12, 11, 10, 9, 8, 7, 6,
            5, 4, 3, 2, 1),
        Table(4, "126 111 99 90 81…",
            126, 111, 99, 90, 81, 72, 63, 54, 45, 36, 27, 18, 9),
        Table(5, "96 81 69 60 51…",
            96, 81, 69, 60, 51, 42, 33, 24, 15, 6),
        Table(6, "66 51 39 30 21…",
            66, 51, 39, 30, 21, 12, 3),
        Table(7, "50 48 47 46 45…",
            50, 48, 47, 46, 45, 44, 43, 42, 41, 40, 39, 38, 37, 36, 35,
            34, 33, 32, 31, 30, 29, 28, 27, 26, 25, 24, 23, 22, 21, 20,
            19, 18, 17, 16, 15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1),
        Table(8, "140 128 120 112 104…",
            140, 128, 120, 112, 104, 100, 96, 92, 88, 84, 80, 76, 72, 68, 64,
            60, 56, 52, 48, 44, 40, 36, 32, 28, 24, 20, 16, 12, 8, 4),
        Table(9, "28 27 26 25 24…",
            28, 27, 26, 25, 24, 23, 22, 21, 20, 19, 18, 17, 16, 15, 14,
            13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1),
    ];

    // One qualification-table row. Cells are КМС/I/II/III/III-junior for the time half then the points
    // half; 0 means "not attainable at this rank" and is stored as null.
    private static RankQualificationRow Qual(
        int order, int rank,
        int tKms, int tFirst, int tSecond, int tThird, int tThirdJ,
        int pKms, int pFirst, int pSecond, int pThird, int pThirdJ) => new()
    {
        Order = order,
        Rank = rank,
        TimeKms = N(tKms), TimeFirst = N(tFirst), TimeSecond = N(tSecond), TimeThird = N(tThird), TimeThirdJunior = N(tThirdJ),
        PointsKms = N(pKms), PointsFirst = N(pFirst), PointsSecond = N(pSecond), PointsThird = N(pThird), PointsThirdJunior = N(pThirdJ),
    };

    private static int? N(int v) => v == 0 ? null : v;

    /// <summary>
    /// The canonical rank qualification table (Додаток 89) seeded on first run. Rows are course-rank
    /// thresholds (high to low); time cells are the max % of the leader's time, points cells the min % of
    /// the leader's score. Editable afterwards on the Ranks page. The 250-rank III-junior time cell is 209
    /// (the official text prints 109, an evident typo among its ~204/170 neighbours).
    /// </summary>
    private static readonly IReadOnlyList<RankQualificationRow> DefaultRankQualification =
    [
        //        rank | КМС  I    II   III  IIIю |КМС  I    II   III  IIIю
        Qual(0,  1200,  131, 147, 174, 209, 0,    74,  66,  54,  49,  0),
        Qual(1,  1100,  129, 144, 170, 204, 0,    76,  68,  56,  50,  0),
        Qual(2,  1000,  126, 141, 166, 199, 0,    78,  70,  58,  51,  0),
        Qual(3,   800,  123, 138, 162, 194, 0,    82,  72,  60,  52,  0),
        Qual(4,   630,  120, 135, 158, 189, 0,    84,  74,  62,  53,  0),
        Qual(5,   500,  117, 132, 154, 184, 224,  86,  76,  64,  54,  46),
        Qual(6,   400,  114, 129, 150, 179, 219,  88,  78,  66,  55,  47),
        Qual(7,   320,  111, 126, 146, 174, 214,  90,  80,  68,  56,  48),
        Qual(8,   250,  108, 123, 142, 170, 209,  92,  82,  70,  57,  49),
        Qual(9,   200,  105, 120, 138, 166, 204,  94,  84,  72,  58,  50),
        Qual(10,  160,  102, 117, 135, 162, 199,  97,  86,  74,  60,  51),
        Qual(11,  120,  100, 114, 132, 158, 194,  100, 88,  76,  62,  52),
        Qual(12,  100,  0,   111, 129, 154, 189,  0,   90,  78,  64,  53),
        Qual(13,   80,  0,   108, 126, 150, 184,  0,   92,  80,  66,  54),
        Qual(14,   63,  0,   105, 123, 146, 179,  0,   94,  82,  68,  55),
        Qual(15,   50,  0,   102, 120, 142, 174,  0,   97,  84,  70,  56),
        Qual(16,   36,  0,   100, 117, 138, 170,  0,   100, 86,  72,  57),
        Qual(17,   32,  0,   0,   114, 135, 166,  0,   0,   88,  74,  58),
        Qual(18,   25,  0,   0,   111, 132, 162,  0,   0,   90,  76,  60),
        Qual(19,   20,  0,   0,   108, 129, 158,  0,   0,   92,  78,  62),
        Qual(20,   16,  0,   0,   105, 126, 154,  0,   0,   95,  80,  64),
        Qual(21,   13,  0,   0,   102, 123, 150,  0,   0,   97,  82,  66),
        Qual(22,   10,  0,   0,   100, 120, 146,  0,   0,   0,   84,  68),
        Qual(23,    8,  0,   0,   0,   117, 142,  0,   0,   0,   86,  70),
        Qual(24,    6,  0,   0,   0,   114, 138,  0,   0,   0,   88,  72),
        Qual(25,    5,  0,   0,   0,   111, 135,  0,   0,   0,   90,  74),
        Qual(26,    4,  0,   0,   0,   108, 132,  0,   0,   0,   94,  78),
        Qual(27,    3,  0,   0,   0,   105, 129,  0,   0,   0,   0,   80),
        Qual(28,    2,  0,   0,   0,   0,   123,  0,   0,   0,   0,   82),
        Qual(29,    1,  0,   0,   0,   0,   114,  0,   0,   0,   0,   88),
    ];
}
