using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.BusinessLogic.Models.Mcp;
using OrientPyx.BusinessLogic.Services.Network;
using OrientPyx.BusinessLogic.Services.Undo;

namespace OrientPyx.BusinessLogic.Services.Mcp;

/// <summary>One change the assistant asks for: its class (after the size rule), what it touches, how it reads.</summary>
public sealed record McpWrite(
    string Tool,
    McpToolClass Class,
    EventSummary? Competition,
    EventDay? Day,
    ChangeSummary Summary,
    bool Undoable,
    Guid? ParticipantId = null);

/// <summary>The answer to a done change.</summary>
public sealed record McpWriteResult(bool Done, string Summary, Guid? StepId, bool Undoable, object? Result);

/// <summary>Information about the call in progress (from the MCP request).</summary>
public sealed class McpCall
{
    public static McpCall None { get; } = new();

    /// <summary>The client's name from <c>initialize</c> (Claude Desktop, gemini-cli …), when known.</summary>
    public string? Client { get; init; }

    /// <summary>Sends a progress notification to the client (no-op when it gave no progress token).</summary>
    public Func<string, Task> Progress { get; init; } = _ => Task.CompletedTask;
}

/// <summary>
/// The one way the assistant changes data (plan §3.4): policy → (confirmation) → one at a time → the competition and
/// day pinned (<see cref="EditorScope"/>), one undo step (<see cref="UndoScope"/>), the «·mcp» origin on the change bus →
/// the same editor methods a page calls → an entry in the agent journal.
/// </summary>
public sealed class McpWriter
{
    private const int MaxRemembered = 200;

    private readonly McpContext _ctx;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stepsGate = new();
    private readonly List<Entry> _entries = [];
    private long _undoCounter;

    internal McpWriter(McpContext ctx) => _ctx = ctx;

    /// <summary>Runs <paramref name="body"/> as the change <paramref name="write"/> — or answers «pending» when the user
    /// has not answered the confirmation within the wait (the change then runs when they allow it).</summary>
    public async Task<object?> RunAsync(McpWrite write, Func<CancellationToken, Task<object?>> body, McpCall call, CancellationToken cancellationToken)
    {
        var verdict = McpToolPolicy.Decide(write.Class, _ctx.Runtime.Mode, _ctx.Runtime.IsAllowedForSession(write.Tool));
        switch (verdict)
        {
            case McpVerdict.Deny:
                throw new McpToolException(
                    "Changes are switched off in OrientPyx (mode «Лише читання»). The user can allow them in «Налаштування → Асистент (MCP)».");
            case McpVerdict.Allow:
                return await ExecuteAsync(write, body, cancellationToken);
            default:
                var request = new McpChangeRequest(
                    Guid.NewGuid(), write.Tool, write.Class == McpToolClass.Risky, write.Undoable, write.Competition?.Name ?? string.Empty,
                    write.Summary, DateTimeOffset.Now + _ctx.Confirmations.Expiry);
                return await _ctx.Confirmations.RequestAsync(request, token => ExecuteAsync(write, body, token), call, cancellationToken);
        }
    }

    /// <summary>The assistant's own changes on <paramref name="competition"/> in this session, oldest first.</summary>
    public IReadOnlyList<McpChange> Changes(string competition)
    {
        lock (_stepsGate)
            return _entries.Where(e => Same(e.Competition, competition)).Select(e => e.Snapshot()).ToList();
    }

    /// <summary>One of the assistant's changes on <paramref name="competition"/> by the step id its answer gave (or the
    /// step a redo made of it); null when it is not one of them.</summary>
    public McpChange? Find(Guid step, string competition)
    {
        lock (_stepsGate)
            return _entries.FirstOrDefault(e => Same(e.Competition, competition) && (e.Id == step || e.ActiveStep == step))?.Snapshot();
    }

    /// <summary>The assistant's last change on <paramref name="competition"/> that can be undone now.</summary>
    public McpChange? LastUndoable(string competition)
    {
        lock (_stepsGate)
            return _entries.LastOrDefault(e => Same(e.Competition, competition) && e.Undoable && !e.Undone)?.Snapshot();
    }

    /// <summary>The assistant's change on <paramref name="competition"/> undone most recently and not redone yet.</summary>
    public McpChange? LastUndone(string competition)
    {
        lock (_stepsGate)
            return _entries.Where(e => Same(e.Competition, competition) && e.Undone && e.RedoStep is not null)
                .OrderByDescending(e => e.UndoneOrder).FirstOrDefault()?.Snapshot();
    }

    /// <summary>The change <paramref name="id"/> was undone; <paramref name="redoStep"/> = the revert's own step (reverting
    /// it redoes the change), null when it cannot be redone (everything was already put back by someone else).</summary>
    public void MarkUndone(Guid id, Guid? redoStep)
    {
        lock (_stepsGate)
            if (_entries.FirstOrDefault(e => e.Id == id) is { } entry)
            {
                entry.Undone = true;
                entry.RedoStep = redoStep;
                entry.UndoneOrder = ++_undoCounter;
                entry.ChangedAt = DateTimeOffset.Now;
            }
    }

    /// <summary>The change <paramref name="id"/> was redone under <paramref name="newStep"/> (undoing it again reverts that).</summary>
    public void MarkRedone(Guid id, Guid newStep)
    {
        lock (_stepsGate)
            if (_entries.FirstOrDefault(e => e.Id == id) is { } entry)
            {
                entry.Undone = false;
                entry.RedoStep = null;
                entry.ActiveStep = newStep;
                entry.Redone = true;
                entry.ChangedAt = DateTimeOffset.Now;
            }
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    internal async Task<object?> ExecuteAsync(McpWrite write, Func<CancellationToken, Task<object?>> body, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            // A change of a competition needs it still open; one of no competition (create / open one) needs nothing.
            if (write.Competition is not null && !McpContext.SameCompetition(_ctx.Session.CurrentEvent, write.Competition))
                throw new McpToolException(
                    "The OrientPyx window switched to another competition meanwhile; nothing was changed. Call get_context.");

            var step = Guid.NewGuid();
            object? result;
            using (write.Competition is null ? null : McpContext.Pin(write.Competition, write.Day))
            using (write.Undoable ? UndoScope.Enter(step) : UndoScope.Suppress())
            {
                DataChangeOrigin.Current = McpOrigin.For(_ctx.Workspace);
                try
                {
                    result = await body(cancellationToken);
                }
                finally
                {
                    DataChangeOrigin.Current = null;
                }
            }

            // undo / redo change the state of the entry they act on instead of adding one of their own.
            if (write.Tool is not ("undo" or "redo"))
            {
                lock (_stepsGate)
                {
                    _entries.Add(new Entry
                    {
                        Id = step, ActiveStep = step, At = DateTimeOffset.Now, Tool = write.Tool,
                        Competition = write.Competition?.Identifier ?? string.Empty, Summary = write.Summary,
                        Day = write.Day?.Number, Undoable = write.Undoable,
                    });
                    if (_entries.Count > MaxRemembered)
                        _entries.RemoveAt(0);
                }
            }

            var stepId = write.Undoable ? step : (Guid?)null;
            _ctx.Log?.Action($"MCP {write.Tool}: {write.Summary.Text}");
            try
            {
                _ctx.Ui.Record(new McpActionRecord(
                    DateTimeOffset.Now, write.Tool, write.Competition?.Identifier ?? string.Empty, write.Summary, stepId, write.Undoable,
                    write.Day?.Number, write.ParticipantId));
            }
            catch (Exception ex)
            {
                _ctx.Log?.Error("MCP: recording a change in the agent journal", ex);
            }

            return new McpWriteResult(true, write.Summary.Text, stepId, write.Undoable, result);
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed class Entry
    {
        public required Guid Id { get; init; }
        public required DateTimeOffset At { get; init; }
        public required string Tool { get; init; }
        public required string Competition { get; init; }
        public required ChangeSummary Summary { get; init; }
        public int? Day { get; init; }
        public bool Undoable { get; init; }
        public Guid ActiveStep { get; set; }
        public Guid? RedoStep { get; set; }
        public bool Undone { get; set; }
        public bool Redone { get; set; }
        public long UndoneOrder { get; set; }
        public DateTimeOffset? ChangedAt { get; set; }

        public McpChange Snapshot() =>
            new(Id, At, Tool, Competition, Summary.Text, Day, Undoable, ActiveStep, RedoStep, Undone, Redone, ChangedAt) { Details = Summary };
    }
}

/// <summary>
/// One change the assistant made in this session (list_changes): <paramref name="Id"/> is the step id its answer gave;
/// <paramref name="ActiveStep"/> the journal step undoing it reverts (a redo makes a new one); <paramref name="RedoStep"/>
/// the step reverting which redoes it, while it is undone.
/// </summary>
public sealed record McpChange(
    Guid Id,
    DateTimeOffset At,
    string Tool,
    string Competition,
    string Summary,
    int? Day,
    bool Undoable,
    Guid ActiveStep,
    Guid? RedoStep,
    bool Undone,
    bool Redone,
    DateTimeOffset? ChangedAt)
{
    /// <summary>The change's summary as the window shows it (key + arguments).</summary>
    public ChangeSummary? Details { get; init; }

    public bool CanUndo => Undoable && !Undone;

    public bool CanRedo => Undone && RedoStep is not null;
}
