namespace OrientPyx.Presentation.ViewModels.Pages;

/// <summary>
/// The in-flight debounced row saves of a page, so a live-data reload (<see cref="PageViewModelBase.ReloadAsync"/>)
/// can let them finish before it replaces the rows. <c>LoadAsync</c> cancels pending saves on purpose — a session
/// switch discards the previous competition's edits — but a reload triggered by another station's write must
/// not: the operator's last keystrokes would be lost together with the row they typed into. Waiting first also
/// means the reload reads the saved values back, so the fresh rows show what was typed. All calls are made on the
/// UI thread (saves are queued from cell edits and awaited from the reload), so no locking is needed.
/// </summary>
internal sealed class PendingSaves
{
    private readonly HashSet<Task> _tasks = [];

    /// <summary>Registers a fire-and-forget save task. The save swallows its own exceptions, so only completion matters.</summary>
    public void Track(Task task)
    {
        if (!task.IsCompleted)
            _tasks.Add(task);
    }

    /// <summary>Completes once no tracked save is running (including saves queued while waiting).</summary>
    public async Task WaitAsync()
    {
        while (true)
        {
            _tasks.RemoveWhere(t => t.IsCompleted);
            if (_tasks.Count == 0)
                return;
            try
            {
                await Task.WhenAll(_tasks.ToArray());
            }
            catch
            {
                // A faulted save is still a finished save; the loop drops it on the next pass.
            }
        }
    }
}
