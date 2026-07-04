using Microsoft.Extensions.DependencyInjection;
using OrientDesk.BusinessLogic.Disciplines;
using OrientDesk.BusinessLogic.Interfaces;
using OrientDesk.BusinessLogic.Services;

namespace OrientDesk.BusinessLogic.DependencyInjection;

public static class BusinessLogicServiceCollectionExtensions
{
    /// <summary>Registers business-logic services.</summary>
    public static IServiceCollection AddOrientDeskBusinessLogic(this IServiceCollection services)
    {
        // Session/catalog/settings
        services.AddSingleton<IAppSettingsService, AppSettingsService>();
        services.AddSingleton<IEventCatalogService, EventCatalogService>();
        services.AddSingleton<ISessionService, SessionService>();
        services.AddSingleton<ICompetitionEditorService, CompetitionEditorService>();

        // Competition-type strategies (Open/Closed): one class per discipline, resolved by enum.
        // Add a new discipline by adding a strategy class + a registration line here — nothing else.
        services.AddSingleton<IDisciplineStrategy, SetCourseStrategy>();
        services.AddSingleton<IDisciplineStrategy, ScoreByCountStrategy>();
        services.AddSingleton<IDisciplineStrategy, ScoreByTimeStrategy>();
        services.AddSingleton<IDisciplineStrategy, RogaineStrategy>();
        services.AddSingleton<IDisciplineStrategy, MixedStrategy>();
        services.AddSingleton<IDisciplineStrategyProvider, DisciplineStrategyProvider>();

        // IOF XML import (shared by control-point and course/group imports)
        services.AddSingleton<IIofXmlParser, IofXmlParser>();

        // UOF participant import (the Ukrainian registration export)
        services.AddSingleton<IUofXmlParser, UofXmlParser>();

        // CSV participant import (arbitrary delimited file, columns mapped to our fields in the UI)
        services.AddSingleton<ICsvParser, CsvParser>();

        // Tabular export writers (one per format) — the participants table's view can be saved out.
        // The CSV writer is pure text; the .xlsx writer needs a library and is registered in DataAccess.
        services.AddSingleton<ITabularWriter, CsvTabularWriter>();

        // Combined course-name splitting (e.g. "ЧЖ55" → "Ч55", "Ж55") for the import splitter dialog.
        services.AddSingleton<ICourseNameSplitter, CourseNameSplitter>();

        // Chip-readout file parsing (shared: rental-chip import + finish read-out timing). One
        // implementation per timing-system file format; the selector picks the one matching the app-level
        // «тип відмітки» (ReadoutType) setting. Register a new format's parser here + add it to the selector.
        services.AddSingleton<SportIdentCsvReadoutParser>();
        services.AddSingleton<SportTimeCsvReadoutParser>();
        services.AddSingleton<IReadoutParser>(sp => sp.GetRequiredService<SportIdentCsvReadoutParser>());
        services.AddSingleton<IReadoutParserSelector, ReadoutParserSelector>();

        // Course-length calculation (shared: group import today, distance display later)
        services.AddSingleton<ICourseDistanceCalculator, CourseDistanceCalculator>();

        // Start-entry fee calculation (the «Стартові внески» columns on the participants table)
        services.AddSingleton<IEntryFeeCalculator, EntryFeeCalculator>();

        // Results-protocol builder (layer-neutral: raw day results → renderable document). The .docx
        // writer that renders the document needs a library and is registered in DataAccess.
        services.AddSingleton<IResultProtocolBuilder, ResultProtocolBuilder>();

        // Start-protocol builder (layer-neutral: raw day start data → renderable document, by-group or
        // by-minute). Reuses the results-protocol .docx writer (same ResultProtocolDocument model).
        services.AddSingleton<IStartProtocolBuilder, StartProtocolBuilder>();

        // Multi-day summary («Підсумковий залік») builder: aggregates each participant across the counted
        // days and ranks the group per the chosen mode. The two-tier .docx writer is in DataAccess.
        services.AddSingleton<ISummaryProtocolBuilder, SummaryProtocolBuilder>();

        // Split (splits) export builder (layer-neutral: raw day splits → renderable document). The HTML
        // writer that renders the document is registered in DataAccess.
        services.AddSingleton<ISplitExportBuilder, SplitExportBuilder>();

        // Start draw (жеребкування): assigns a start time per competitor (random + "not consecutive").
        services.AddSingleton<IStartDrawService, StartDrawService>();

        return services;
    }
}
