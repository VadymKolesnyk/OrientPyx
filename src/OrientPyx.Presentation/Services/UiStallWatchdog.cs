using System.Diagnostics;
using Avalonia.Threading;
using OrientPyx.BusinessLogic.Interfaces;

namespace OrientPyx.Presentation.Services;

/// <summary>
/// Diagnostics for «Не відповідає»: a pool timer posts a probe to the UI thread twice a second and measures how
/// long it waited to run. A wait past <see cref="Threshold"/> means the UI thread was blocked (or its queue was
/// flooded) for that long; the stall is written to the activity log with what was going on — the workspace role,
/// the running background activities and the gen-2 collections since the last stall — so a freeze the user
/// reports can be matched against the log of that session. Nothing here changes behaviour; it only observes.
/// </summary>
public sealed class UiStallWatchdog : IDisposable
{
    private static readonly TimeSpan Probe = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan Threshold = TimeSpan.FromSeconds(1);

    private readonly IActivityLog _log;
    private readonly IBackgroundActivityService _activities;
    private readonly IWorkspaceMode _mode;
    private readonly Timer _timer;
    private int _probeInFlight;
    private int _gen2AtLastReport = GC.CollectionCount(2);

    public UiStallWatchdog(IActivityLog log, IBackgroundActivityService activities, IWorkspaceMode mode)
    {
        _log = log;
        _activities = activities;
        _mode = mode;
        _timer = new Timer(_ => SendProbe(), null, Probe, Probe);
    }

    private void SendProbe()
    {
        // One probe at a time: while the UI thread is stuck the earlier probe is still queued and already
        // measures the whole stall; stacking more would only flood the queue it is waiting on.
        if (Interlocked.CompareExchange(ref _probeInFlight, 1, 0) != 0)
            return;

        var waited = Stopwatch.StartNew();
        Dispatcher.UIThread.Post(() =>
        {
            waited.Stop();
            Interlocked.Exchange(ref _probeInFlight, 0);
            if (waited.Elapsed >= Threshold)
                Report(waited.Elapsed);
        });
    }

    // Runs on the UI thread (the probe's continuation), so the activity list may be read directly.
    private void Report(TimeSpan stall)
    {
        var gen2 = GC.CollectionCount(2);
        var gen2Delta = gen2 - _gen2AtLastReport;
        _gen2AtLastReport = gen2;

        // Every registered activity is live (the block unregisters a stopped one), so list them all.
        var running = _activities.Activities.Select(a => $"{a.Title}={a.State}").ToList();
        var activities = running.Count == 0 ? "none" : string.Join(", ", running);
        var memoryMb = Environment.WorkingSet / (1024 * 1024);

        _log.Info(
            $"UI thread stalled for {stall.TotalSeconds:0.0} s — role={_mode.Role}, activities: {activities}, " +
            $"gen2 GCs since last stall={gen2Delta}, working set={memoryMb} MB");
    }

    public void Dispose() => _timer.Dispose();
}
