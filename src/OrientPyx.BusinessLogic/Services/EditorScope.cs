using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Models;

namespace OrientPyx.BusinessLogic.Services;

/// <summary>The competition (and optionally the day) a call works on instead of the session's.</summary>
public sealed record EditorContext(EventSummary Competition, EventDay? Day);

/// <summary>
/// Pins the competition + day of one call, flowing with the async call like <see cref="Undo.UndoScope"/>: the
/// assistant (MCP) works on the competition that was open when its call began and on the day it named, without
/// switching what the window shows. <c>CompetitionEditorService</c> and <c>EditHistoryService</c> read it before the
/// session, so a competition switched in the window half-way through a batch never receives the rest of it.
/// No scope ⇒ the session, exactly as before.
/// </summary>
public static class EditorScope
{
    private static readonly AsyncLocal<EditorContext?> CurrentContext = new();

    /// <summary>The pinned competition + day of the call in progress; null = the session's.</summary>
    public static EditorContext? Current => CurrentContext.Value;

    /// <summary>Pins <paramref name="competition"/> (and <paramref name="day"/>, null = the session's) until disposed.</summary>
    public static IDisposable Enter(EventSummary competition, EventDay? day)
    {
        ArgumentNullException.ThrowIfNull(competition);
        var previous = CurrentContext.Value;
        CurrentContext.Value = new EditorContext(competition, day);
        return new Scope(previous);
    }

    private sealed class Scope(EditorContext? previous) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                CurrentContext.Value = previous;
        }
    }
}
