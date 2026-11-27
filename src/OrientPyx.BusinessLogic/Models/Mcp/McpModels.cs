namespace OrientPyx.BusinessLogic.Models.Mcp;

/// <summary>
/// A human description of one change of the assistant — for the confirmation bar, the agent journal and the answer.
/// <paramref name="Key"/> is a localisation key (<c>Mcp.Change.*</c>, formatted with <paramref name="Args"/>) the window
/// shows; <paramref name="Text"/> is the same in English for the assistant and the activity log.
/// </summary>
public sealed record ChangeSummary(string Key, IReadOnlyList<string> Args, string Text, IReadOnlyList<ChangeRow> Rows)
{
    /// <summary>The most rows a summary carries («Деталі» shows up to this many).</summary>
    public const int MaxRows = 200;

    public static ChangeSummary Of(string key, string text, params string[] args) => new(key, args, text, []);

    public ChangeSummary WithRows(IEnumerable<ChangeRow> rows) => this with { Rows = rows.Take(MaxRows).ToList() };

    /// <summary>An earlier change this one acts on (undo / redo): the window shows it in its own words as {0}.</summary>
    public ChangeSummary? Inner { get; init; }
}

/// <summary>One line of «Деталі»: who / what, which field, the value before and after.</summary>
public sealed record ChangeRow(string Subject, string Field, string? Old, string? New);

/// <summary>What the assistant asks the user to allow (shown in the confirmation bar of the window).</summary>
public sealed record McpChangeRequest(
    Guid Id,
    string Tool,
    bool Risky,
    bool Undoable,
    string CompetitionName,
    ChangeSummary Summary,
    DateTimeOffset ExpiresAt);

/// <summary>The user's answer in the confirmation bar.</summary>
public sealed record McpDecision(bool Approved, bool RememberForSession = false)
{
    public static readonly McpDecision Rejected = new(false);
}

/// <summary>One done change of the assistant, for the agent journal («MCP» in the activities block).</summary>
public sealed record McpActionRecord(
    DateTimeOffset At,
    string Tool,
    string CompetitionIdentifier,
    ChangeSummary Summary,
    Guid? StepId,
    bool Undoable,
    int? DayNumber,
    Guid? ParticipantId = null);

/// <summary>One background process of the window (the top-bar «Процеси» list) as the assistant reads it.</summary>
public sealed record McpActivityInfo(string Title, string Status, string State, bool Paused, bool Problem);

/// <summary>Where «show_in_app» takes the window.</summary>
public sealed record McpShowTarget(string Page, int? DayNumber, Guid? ParticipantId);

/// <summary>One call in the activity of the server (for the «MCP» activity and the log).</summary>
public sealed record McpCallRecord(DateTimeOffset At, string Tool, bool Ok, TimeSpan Duration, string? Client);
