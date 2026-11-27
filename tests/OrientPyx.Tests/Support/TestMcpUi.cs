using System.Collections.Concurrent;
using OrientPyx.BusinessLogic.Disciplines;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.BusinessLogic.Models.Mcp;

namespace OrientPyx.Tests.Support;

/// <summary>
/// The window's side of the assistant for the core tests: answers every confirmation with <see cref="Decide"/> (allow by
/// default; a test may hold the answer back with a task), remembers what was asked and recorded, labels from the keys.
/// </summary>
public sealed class TestMcpUi : IMcpUi, IOutputLabels
{
    public Func<McpChangeRequest, Task<McpDecision>> Decide { get; set; } = _ => Task.FromResult(new McpDecision(true));

    public ConcurrentQueue<McpChangeRequest> Requests { get; } = new();

    public ConcurrentQueue<McpActionRecord> Records { get; } = new();

    public List<McpShowTarget> Shown { get; } = [];

    public void Reset()
    {
        Decide = _ => Task.FromResult(new McpDecision(true));
        Requests.Clear();
        Records.Clear();
        Shown.Clear();
        Activities.Clear();
    }

    public async Task<McpDecision> ConfirmAsync(McpChangeRequest request, CancellationToken cancellationToken)
    {
        Requests.Enqueue(request);
        return await Decide(request);
    }

    public Task<bool> OpenCompetitionAsync(EventSummary competition, int? dayNumber, CancellationToken cancellationToken) =>
        Task.FromResult(false);

    public Task<bool> ShowAsync(McpShowTarget target, CancellationToken cancellationToken)
    {
        Shown.Add(target);
        return Task.FromResult(true);
    }

    public string? GetHelp(string key) => key.StartsWith("Help.", StringComparison.Ordinal) ? "help " + key : null;

    public IReadOnlyList<string> HelpKeys => ["Help.Settings.Mcp.Title", "Help.Settings.Mcp.What"];

    public string Text(string key) => key;

    public void Record(McpActionRecord record) => Records.Enqueue(record);

    public void CallFinished(McpCallRecord record)
    {
    }

    /// <summary>What <see cref="ActivitiesAsync"/> answers (a test sets it).</summary>
    public List<McpActivityInfo> Activities { get; } = [];

    public Task<IReadOnlyList<McpActivityInfo>> ActivitiesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<McpActivityInfo>>(Activities.ToList());

    public IOutputLabels Labels => this;

    public ProtocolLabels ResultProtocol() => Support.Labels.Make<ProtocolLabels>();

    public StartProtocolLabels StartProtocol(StartProtocolKind kind) => Support.Labels.Make<StartProtocolLabels>();

    public SummaryProtocolLabels SummaryProtocol() => Support.Labels.Make<SummaryProtocolLabels>();

    public SplitExportLabels Splits() => Support.Labels.Make<SplitExportLabels>();

    public StatementLabels Statement() => Support.Labels.Make<StatementLabels>();

    public string DisciplineName(DisciplineId id) => id.Value;

    public string FileName(string kind, string? competitionName, string extension, EventDay? day, bool multiDay, DateTimeOffset? date = null) =>
        $"{kind} - {competitionName}{(day is not null ? $" - day{day.Number}" : "")}.{extension}";
}
