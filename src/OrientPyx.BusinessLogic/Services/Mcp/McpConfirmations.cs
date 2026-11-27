using System.Collections.Concurrent;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models.Mcp;

namespace OrientPyx.BusinessLogic.Services.Mcp;

/// <summary>The answer while the user has not clicked yet (plan М5).</summary>
public sealed record McpPending(string Status, Guid ConfirmationId, string Message);

/// <summary>
/// Changes waiting for «Дозволити» in the window (plan §7.3 / М5). A call waits at most <see cref="Wait"/> — shorter than the
/// clients' own request time-out — and then answers <c>pending</c>; the change still runs the moment the user allows it
/// (its result is kept for <c>await_confirmation</c>), and nothing runs after a refusal or <see cref="Expiry"/>.
/// </summary>
public sealed class McpConfirmations
{
    private readonly Func<IMcpUi> _ui;
    private readonly McpRuntime _runtime;
    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();

    public McpConfirmations(Func<IMcpUi> ui, McpRuntime runtime)
    {
        _ui = ui;
        _runtime = runtime;
    }

    /// <summary>How long one call waits for the answer (stays under the 60 s request time-out of TS-SDK clients).</summary>
    public TimeSpan Wait { get; set; } = TimeSpan.FromSeconds(40);

    /// <summary>How long a request stays in the bar; the outcome of a done one is kept as long.</summary>
    public TimeSpan Expiry { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>How often a waiting call tells the client it is still alive.</summary>
    public TimeSpan ProgressEvery { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Requests that still wait for the user (the bar shows them).</summary>
    public int PendingCount => _entries.Values.Count(e => !e.Outcome.IsCompleted);

    public async Task<object?> RequestAsync(
        McpChangeRequest request, Func<CancellationToken, Task<object?>> execute, McpCall call, CancellationToken cancellationToken)
    {
        Sweep();
        var entry = new Entry(request, Expiry);
        _entries[request.Id] = entry;
        _ = RunAsync(entry, execute);
        return await WaitAsync(entry, call, cancellationToken);
    }

    /// <summary><c>await_confirmation</c>: waits again for a pending request.</summary>
    public Task<object?> AwaitAsync(Guid confirmationId, McpCall call, CancellationToken cancellationToken)
    {
        Sweep();
        if (!_entries.TryGetValue(confirmationId, out var entry))
            throw new McpToolException("Unknown or expired confirmation_id (requests expire after 10 minutes). Ask again.");
        return WaitAsync(entry, call, cancellationToken);
    }

    /// <summary>Drops every waiting request (the server stops): their bars go away, nothing runs.</summary>
    public void CancelAll()
    {
        foreach (var e in _entries.Values)
            e.Cancel();
        _entries.Clear();
    }

    private async Task RunAsync(Entry entry, Func<CancellationToken, Task<object?>> execute)
    {
        try
        {
            McpDecision decision;
            try
            {
                decision = await _ui().ConfirmAsync(entry.Request, entry.Token);
            }
            catch (OperationCanceledException)
            {
                decision = McpDecision.Rejected;
            }

            if (!decision.Approved)
            {
                entry.Fail(new McpToolException(entry.Token.IsCancellationRequested
                    ? "Nobody confirmed the change in OrientPyx in time; nothing was changed."
                    : "The user did not allow the change in OrientPyx; nothing was changed."));
                return;
            }

            if (decision.RememberForSession)
                _runtime.AllowForSession(entry.Request.Tool);
            entry.Succeed(await execute(CancellationToken.None));
        }
        catch (Exception ex)
        {
            entry.Fail(ex);
        }
    }

    private async Task<object?> WaitAsync(Entry entry, McpCall call, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.Now + Wait;
        while (!entry.Outcome.IsCompleted)
        {
            var left = deadline - DateTimeOffset.Now;
            if (left <= TimeSpan.Zero)
                break;
            var slice = left < ProgressEvery ? left : ProgressEvery;
            await Task.WhenAny(entry.Outcome, Task.Delay(slice, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
            if (!entry.Outcome.IsCompleted)
                await call.Progress("Waiting for the user to click «Дозволити» in the OrientPyx window…");
        }

        if (entry.Outcome.IsCompleted)
        {
            _entries.TryRemove(entry.Request.Id, out _);
            return await entry.Outcome;
        }

        return new McpPending(
            "pending",
            entry.Request.Id,
            "The user has not answered yet: the change waits for «Дозволити» in the OrientPyx window. Tell the user to look at " +
            "OrientPyx, then call await_confirmation with this confirmation_id. Do not repeat the change.");
    }

    private void Sweep()
    {
        var now = DateTimeOffset.Now;
        foreach (var (id, e) in _entries)
            if (e.Outcome.IsCompleted && e.CompletedAt is { } done && now - done > Expiry)
                _entries.TryRemove(id, out _);
    }

    private sealed class Entry
    {
        private readonly TaskCompletionSource<object?> _outcome = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _expiry;

        public Entry(McpChangeRequest request, TimeSpan expiry)
        {
            Request = request;
            _expiry = new CancellationTokenSource(expiry);
            // A refused change nobody waits for any more must not surface as an unobserved task exception (the app
            // reports those as crashes).
            _outcome.Task.ContinueWith(t => _ = t.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        public McpChangeRequest Request { get; }
        public CancellationToken Token => _expiry.Token;
        public Task<object?> Outcome => _outcome.Task;
        public DateTimeOffset? CompletedAt { get; private set; }

        public void Succeed(object? value)
        {
            CompletedAt = DateTimeOffset.Now;
            _outcome.TrySetResult(value);
        }

        public void Fail(Exception ex)
        {
            CompletedAt = DateTimeOffset.Now;
            _outcome.TrySetException(ex);
        }

        public void Cancel()
        {
            _expiry.Cancel();
            Fail(new McpToolException("The assistant was switched off in OrientPyx; nothing was changed."));
        }
    }
}
