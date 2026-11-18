using System.Collections.Concurrent;

namespace OrientPyx.BusinessLogic.Services.Undo;

/// <summary>
/// The undo step (Ctrl+Z) the write in progress belongs to, flowing with the async call like
/// <see cref="Network.DataChangeOrigin"/>: the UI enters it around the editor call of one user action, the host's RPC
/// dispatcher enters the step a station sent in its header, and the change recorder in DataAccess reads it in
/// <c>SaveChanges</c>. No dependencies, so every layer can use it.
/// </summary>
/// <remarks>
/// <b>Leasing.</b> <see cref="Enter"/> counts the open entries of each step and the returned scope gives its entry
/// back. A task that inherited the async-local and outlived its entry (fire-and-forget, a timer started inside the
/// step) writes under a step nobody has open any more — <see cref="IsActive"/> is false and the recorder keeps such
/// a change out of the step.
/// </remarks>
public static class UndoScope
{
    private static readonly AsyncLocal<Guid?> CurrentStep = new();
    private static readonly AsyncLocal<string?> CurrentOwner = new();
    private static readonly ConcurrentDictionary<Guid, int> OpenEntries = new();

    /// <summary>The step of the write in progress; null = a write outside any step (a system write).</summary>
    public static Guid? Current => CurrentStep.Value;

    /// <summary>Who the step belongs to: the station's token on a host, null = this machine.</summary>
    public static string? Owner => CurrentOwner.Value;

    /// <summary>Enters <paramref name="stepId"/> until the returned scope is disposed (which restores the previous one).</summary>
    public static IDisposable Enter(Guid stepId, string? owner = null)
    {
        var scope = new Scope(CurrentStep.Value, CurrentOwner.Value, stepId);
        OpenEntries.AddOrUpdate(stepId, 1, static (_, n) => n + 1);
        CurrentStep.Value = stepId;
        CurrentOwner.Value = owner;
        return scope;
    }

    /// <summary>No step until the returned scope is disposed: reads and background work started here never join one.</summary>
    public static IDisposable Suppress()
    {
        var scope = new Scope(CurrentStep.Value, CurrentOwner.Value, null);
        CurrentStep.Value = null;
        CurrentOwner.Value = null;
        return scope;
    }

    /// <summary>True while at least one <see cref="Enter"/> of <paramref name="stepId"/> is still open.</summary>
    public static bool IsActive(Guid stepId) => OpenEntries.TryGetValue(stepId, out var n) && n > 0;

    private sealed class Scope : IDisposable
    {
        private readonly Guid? _previousStep;
        private readonly string? _previousOwner;
        private readonly Guid? _leased;
        private int _disposed;

        public Scope(Guid? previousStep, string? previousOwner, Guid? leased)
        {
            _previousStep = previousStep;
            _previousOwner = previousOwner;
            _leased = leased;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            CurrentStep.Value = _previousStep;
            CurrentOwner.Value = _previousOwner;
            if (_leased is not { } id)
                return;
            while (OpenEntries.TryGetValue(id, out var n))
            {
                if (n <= 1)
                {
                    if (OpenEntries.TryRemove(new KeyValuePair<Guid, int>(id, n)))
                        return;
                }
                else if (OpenEntries.TryUpdate(id, n - 1, n))
                {
                    return;
                }
            }
        }
    }
}
