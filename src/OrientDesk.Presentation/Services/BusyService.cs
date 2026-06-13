using System.ComponentModel;
using OrientDesk.BusinessLogic.Interfaces;

namespace OrientDesk.Presentation.Services;

/// <summary>Counter-based <see cref="IBusyService"/>. Busy while any operation is running.</summary>
public sealed class BusyService : IBusyService
{
    private int _activeCount;
    private readonly IActivityLog _log;

    public BusyService(IActivityLog log) => _log = log;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsBusy => _activeCount > 0;

    // The operation runs on a thread-pool thread (Task.Run), not the UI thread. SQLite has no
    // real async I/O — EF Core's *Async methods over Microsoft.Data.Sqlite complete synchronously —
    // so awaiting them on the UI thread would block it and the busy overlay would never paint.
    // Offloading here keeps the UI thread free; callers must therefore do all UI-state writes
    // (ObservableCollection edits, navigation) AFTER awaiting RunAsync, back on the UI thread.
    public async Task<T> RunAsync<T>(Func<Task<T>> operation)
    {
        Enter();
        try
        {
            return await Task.Run(operation);
        }
        catch (Exception ex)
        {
            _log.Error("Background operation failed", ex);
            throw;
        }
        finally
        {
            Exit();
        }
    }

    public async Task RunAsync(Func<Task> operation)
    {
        Enter();
        try
        {
            await Task.Run(operation);
        }
        catch (Exception ex)
        {
            _log.Error("Background operation failed", ex);
            throw;
        }
        finally
        {
            Exit();
        }
    }

    private void Enter()
    {
        var wasBusy = IsBusy;
        _activeCount++;
        if (!wasBusy)
            RaiseIsBusy();
    }

    private void Exit()
    {
        _activeCount = Math.Max(0, _activeCount - 1);
        if (!IsBusy)
            RaiseIsBusy();
    }

    private void RaiseIsBusy() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsBusy)));
}
