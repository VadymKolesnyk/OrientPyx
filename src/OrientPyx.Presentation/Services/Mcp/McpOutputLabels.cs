using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.Localization;
using OrientPyx.Presentation.ViewModels.Pages;

namespace OrientPyx.Presentation.Services.Mcp;

/// <summary>The labels the pages pass to the document builders, for the assistant's export (the same factories).</summary>
public sealed class McpOutputLabels(ILocalizationService localization, IDisciplineStrategyProvider strategies) : IOutputLabels
{
    public ProtocolLabels ResultProtocol() => ProtocolsViewModel.CreateLabels(localization);

    public StartProtocolLabels StartProtocol(StartProtocolKind kind) => StartProtocolsViewModel.CreateLabels(localization, kind);

    public SummaryProtocolLabels SummaryProtocol() => SummaryProtocolsViewModel.CreateLabels(localization);

    public SplitExportLabels Splits() => SplitsExportViewModel.CreateLabels(localization);

    public StatementLabels Statement() => StatementFlow.CreateLabels(localization);

    public string DisciplineName(DisciplineId id) => DisciplineLabels.Of(strategies.For(id), localization);

    public string FileName(string kind, string? competitionName, string extension, EventDay? day, bool multiDay, DateTimeOffset? date = null)
    {
        var typeKey = kind switch
        {
            "result_protocol" => "Protocols.NamePart",
            "start_protocol" => "StartProtocols.NamePart.Regular",
            "judges_start_protocol" => "StartProtocols.NamePart.Judges",
            "summary_protocol" => "SummaryProtocol.NamePart",
            "splits_html" => "Splits.NamePart",
            "iof_results" => "IofExport.NamePart.Results",
            "iof_start_list" => "IofExport.NamePart.Start",
            "statement" => "Statement.NamePart",
            // A plugin report: its own name key (the plugin report page names the file by it too).
            _ when kind.StartsWith("key:", StringComparison.Ordinal) => kind[4..],
            _ => "Export.NamePart",
        };
        return ExportFileName.Build(localization, typeKey, competitionName, extension, day, multiDay, date);
    }
}
