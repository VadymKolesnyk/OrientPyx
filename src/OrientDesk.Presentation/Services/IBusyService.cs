using System.ComponentModel;

namespace OrientDesk.Presentation.Services;

/// <summary>
/// Tracks whether a long-running operation is in progress so a single global loader overlay
/// can be shown. Uses a counter, so nested/overlapping operations stay busy until the last
/// one finishes.
/// </summary>
public interface IBusyService : INotifyPropertyChanged
{
    bool IsBusy { get; }

    /// <summary>Runs the operation while keeping the app marked busy. Returns the result.</summary>
    Task<T> RunAsync<T>(Func<Task<T>> operation);

    /// <summary>Runs the operation while keeping the app marked busy.</summary>
    Task RunAsync(Func<Task> operation);
}
