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

        // Chip-readout file parsing (shared: rental-chip import today, participant timing later).
        // One implementation per file format; register the new one here when another format is added.
        services.AddSingleton<IReadoutParser, SportIdentCsvReadoutParser>();

        // Course-length calculation (shared: group import today, distance display later)
        services.AddSingleton<ICourseDistanceCalculator, CourseDistanceCalculator>();

        // Start-entry fee calculation (the «Стартові внески» columns on the participants table)
        services.AddSingleton<IEntryFeeCalculator, EntryFeeCalculator>();

        // Results-protocol builder (layer-neutral: raw day results → renderable document). The .docx
        // writer that renders the document needs a library and is registered in DataAccess.
        services.AddSingleton<IResultProtocolBuilder, ResultProtocolBuilder>();

        // Split (splits) export builder (layer-neutral: raw day splits → renderable document). The HTML
        // writer that renders the document is registered in DataAccess.
        services.AddSingleton<ISplitExportBuilder, SplitExportBuilder>();

        // Start draw (жеребкування): assigns a start time per competitor (random + "not consecutive").
        services.AddSingleton<IStartDrawService, StartDrawService>();

        // Placeholder competition data service (dashboard)
        services.AddSingleton<ICompetitionService, CompetitionService>();
        return services;
    }
}
