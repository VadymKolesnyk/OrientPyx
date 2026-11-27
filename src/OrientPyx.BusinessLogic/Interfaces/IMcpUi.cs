using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.BusinessLogic.Models.Mcp;

namespace OrientPyx.BusinessLogic.Interfaces;

/// <summary>
/// What only the window can do for the assistant (implemented in Presentation; every member marshals to the UI thread
/// itself): ask the user to allow a change, open a competition, show a page, give the program's own texts.
/// </summary>
public interface IMcpUi
{
    /// <summary>Shows the confirmation bar and completes when the user answers (or <paramref name="cancellationToken"/>
    /// fires — the request expired, the bar goes away). Never throws for a closed window: answers «Відхилити».</summary>
    Task<McpDecision> ConfirmAsync(McpChangeRequest request, CancellationToken cancellationToken);

    /// <summary>Opens <paramref name="competition"/> in the window as the competition picker does, on day
    /// <paramref name="dayNumber"/> (null = the picker's choice). False when it could not be opened.</summary>
    Task<bool> OpenCompetitionAsync(EventSummary competition, int? dayNumber, CancellationToken cancellationToken);

    /// <summary>Navigates the window (page, day, a participant's row). False when the page is unknown or not available.</summary>
    Task<bool> ShowAsync(McpShowTarget target, CancellationToken cancellationToken);

    /// <summary>The program's help text (<c>Help.*</c>) in the current language; null when there is none.</summary>
    string? GetHelp(string key);

    /// <summary>Every help key.</summary>
    IReadOnlyList<string> HelpKeys { get; }

    /// <summary>A localised text of the window by key (prompt titles, labels); the key itself when unknown.</summary>
    string Text(string key);

    /// <summary>A change the assistant made — appended to the agent journal.</summary>
    void Record(McpActionRecord record);

    /// <summary>A call finished — for the «MCP» activity (counts, last call).</summary>
    void CallFinished(McpCallRecord record);

    /// <summary>The background processes running in the window (finish auto-read, results monitor, online results, the
    /// assistant itself …): title, status line, state, failing or not — read only.</summary>
    Task<IReadOnlyList<McpActivityInfo>> ActivitiesAsync(CancellationToken cancellationToken);

    /// <summary>The labels the Word / HTML outputs need, built from the localisation (export tool).</summary>
    IOutputLabels Labels { get; }
}

/// <summary>The window's labels for the documents the assistant exports (the same the pages pass to the builders).</summary>
public interface IOutputLabels
{
    ProtocolLabels ResultProtocol();

    StartProtocolLabels StartProtocol(StartProtocolKind kind);

    SummaryProtocolLabels SummaryProtocol();

    SplitExportLabels Splits();

    StatementLabels Statement();

    /// <summary>The discipline's name in the current language («тип дистанції» of the protocol header).</summary>
    string DisciplineName(Disciplines.DisciplineId id);

    /// <summary>The file name the page would suggest: «&lt;kind&gt; - &lt;competition&gt; - [День N - ]&lt;date&gt;.&lt;ext&gt;».
    /// <paramref name="kind"/>: result_protocol, start_protocol, judges_start_protocol, summary_protocol, splits_html,
    /// iof_results, iof_start_list, statement, participants.</summary>
    string FileName(string kind, string? competitionName, string extension, EventDay? day, bool multiDay, DateTimeOffset? date = null);
}
