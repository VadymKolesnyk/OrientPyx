using System.Collections.ObjectModel;
using System.ComponentModel;

namespace OrientPyx.Presentation.Services;

/// <summary>
/// Tracks whether a long-running operation is in progress so a single global loader overlay
/// can be shown. Uses a counter, so nested/overlapping operations stay busy until the last
/// one finishes. Operations can optionally report step-by-step progress lines that the overlay
/// renders live (see the <see cref="IProgressReporter"/> overloads).
/// </summary>
public interface IBusyService : INotifyPropertyChanged
{
    bool IsBusy { get; }

    /// <summary>
    /// Progress lines for the operation currently shown in the overlay, oldest first. Cleared when
    /// the overlay goes idle. UI-thread only; bound directly by the overlay.
    /// </summary>
    ObservableCollection<string> Progress { get; }

    /// <summary>Runs the operation while keeping the app marked busy. Returns the result.</summary>
    Task<T> RunAsync<T>(Func<Task<T>> operation);

    /// <summary>Runs the operation while keeping the app marked busy.</summary>
    Task RunAsync(Func<Task> operation);

    /// <summary>
    /// Runs the operation while keeping the app busy, handing it a reporter so it can push progress
    /// lines onto the overlay. The lines are cleared automatically when the operation finishes.
    /// </summary>
    Task<T> RunAsync<T>(Func<IProgressReporter, Task<T>> operation);

    /// <summary>Progress-reporting variant of <see cref="RunAsync(Func{Task})"/>.</summary>
    Task RunAsync(Func<IProgressReporter, Task> operation);
}
