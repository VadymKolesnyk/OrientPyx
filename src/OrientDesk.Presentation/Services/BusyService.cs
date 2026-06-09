using System.ComponentModel;

namespace OrientDesk.Presentation.Services;

/// <summary>Counter-based <see cref="IBusyService"/>. Busy while any operation is running.</summary>
public sealed class BusyService : IBusyService
{
    private int _activeCount;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsBusy => _activeCount > 0;

    public async Task<T> RunAsync<T>(Func<Task<T>> operation)
    {
        Enter();
        try
        {
            return await operation();
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
            await operation();
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
