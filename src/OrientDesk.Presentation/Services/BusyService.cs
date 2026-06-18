using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Threading;
using OrientDesk.BusinessLogic.Interfaces;

namespace OrientDesk.Presentation.Services;

/// <summary>Counter-based <see cref="IBusyService"/>. Busy while any operation is running.</summary>
public sealed class BusyService : IBusyService
{
    // Safety net: if an operation runs longer than this, stop showing the overlay so the app never
    // looks frozen and the user never has to restart. The operation itself keeps running in the
    // background (SQLite calls can't be safely aborted mid-flight) — we just release the UI. A normal
    // page load or import finishes in well under a second; anything past this is a stuck operation.
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(20);

    private int _activeCount;
    private readonly IActivityLog _log;

    public BusyService(IActivityLog log) => _log = log;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsBusy => _activeCount > 0;

    public ObservableCollection<string> Progress { get; } = [];

    // The operation runs on a thread-pool thread (Task.Run), not the UI thread. SQLite has no
    // real async I/O — EF Core's *Async methods over Microsoft.Data.Sqlite complete synchronously —
    // so awaiting them on the UI thread would block it and the busy overlay would never paint.
    // Offloading here keeps the UI thread free; callers must therefore do all UI-state writes
    // (ObservableCollection edits, navigation) AFTER awaiting RunAsync, back on the UI thread.
    public Task<T> RunAsync<T>(Func<Task<T>> operation) => RunAsync(_ => operation());

    public Task RunAsync(Func<Task> operation) => RunAsync(_ => operation());

    public async Task<T> RunAsync<T>(Func<IProgressReporter, Task<T>> operation)
    {
        var reporter = Enter();
        var releasedByTimeout = false;
        try
        {
            var work = Task.Run(() => operation(reporter));
            releasedByTimeout = await WaitWithTimeoutAsync(work);
            return await work;
        }
        catch (Exception ex)
        {
            _log.Error("Background operation failed", ex);
            throw;
        }
        finally
        {
            if (!releasedByTimeout)
                Exit();
        }
    }

    public async Task RunAsync(Func<IProgressReporter, Task> operation)
    {
        var reporter = Enter();
        var releasedByTimeout = false;
        try
        {
            var work = Task.Run(() => operation(reporter));
            releasedByTimeout = await WaitWithTimeoutAsync(work);
            await work;
        }
        catch (Exception ex)
        {
            _log.Error("Background operation failed", ex);
            throw;
        }
        finally
        {
            if (!releasedByTimeout)
                Exit();
        }
    }

    // Waits for the work, but if it outlives OperationTimeout, releases the overlay early (Exit) and
    // logs, returning true so the caller's finally does not Exit a second time. The work itself is
    // still awaited by the caller afterwards. Guarantees a stuck operation can never leave the app
    // showing the loader forever.
    private async Task<bool> WaitWithTimeoutAsync(Task work)
    {
        var finished = await Task.WhenAny(work, Task.Delay(OperationTimeout));
        if (finished == work)
            return false;

        _log.Info($"Background operation exceeded {OperationTimeout.TotalSeconds:0}s — releasing the busy overlay; it continues in the background.");
        Exit();
        return true;
    }

    private DispatcherProgressReporter Enter()
    {
        var wasBusy = IsBusy;
        _activeCount++;
        if (!wasBusy)
        {
            ClearProgress();
            RaiseIsBusy();
        }
        return new DispatcherProgressReporter(Progress);
    }

    private void Exit()
    {
        _activeCount = Math.Max(0, _activeCount - 1);
        if (!IsBusy)
        {
            ClearProgress();
            RaiseIsBusy();
        }
    }

    private void ClearProgress()
    {
        if (Dispatcher.UIThread.CheckAccess())
            Progress.Clear();
        else
            Dispatcher.UIThread.Post(Progress.Clear);
    }

    private void RaiseIsBusy() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsBusy)));

    /// <summary>
    /// Marshals each reported line onto the UI thread and into the bound collection, so an operation
    /// running on a pool thread can update the overlay safely.
    /// </summary>
    private sealed class DispatcherProgressReporter : IProgressReporter
    {
        private readonly ObservableCollection<string> _lines;

        public DispatcherProgressReporter(ObservableCollection<string> lines) => _lines = lines;

        public void Report(string line) => Post(() => _lines.Add(line));

        public void ReportReplace(string line) => Post(() =>
        {
            if (_lines.Count == 0)
                _lines.Add(line);
            else
                _lines[^1] = line;
        });

        private static void Post(Action action)
        {
            if (Dispatcher.UIThread.CheckAccess())
                action();
            else
                Dispatcher.UIThread.Post(action);
        }
    }
}
