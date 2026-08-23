using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace OrientPyx.Presentation.Services;

/// <summary>The run state of a background activity, shown in the top-bar activity popup.</summary>
public enum BackgroundActivityState
{
    /// <summary>Actively doing work (e.g. polling the readout file every N seconds).</summary>
    Running,

    /// <summary>Temporarily suspended by the user; can be resumed without losing its settings.</summary>
    Paused,

    /// <summary>
    /// Still running and retrying, but its work is currently failing (e.g. the online publisher can't reach
    /// the server). Distinct from Paused: the user didn't ask for it, and it needs attention — the top-bar
    /// block turns red so a stalled process can't sit there looking healthy.
    /// </summary>
    Failing,
}

/// <summary>
/// One long-running background process surfaced in the top-bar "running processes" block — today the
/// rental-chip auto-read, later e.g. a finish-line reader or an online-results push. The block knows
/// activities only through this abstraction, so a new kind of process plugs in by implementing it and
/// registering with <see cref="IBackgroundActivityService"/>; the UI never changes (open/closed).
///
/// An activity is a thin façade over whatever actually does the work (a poller, an uploader). It owns
/// its own capabilities — not every process can be paused or has a settings page — so the popup shows
/// only the controls that make sense for each one. The actions are commands so the popup can bind
/// directly; <see cref="IRelayCommand"/> matches the type the MVVM toolkit generates on implementors.
/// Implements <see cref="INotifyPropertyChanged"/> so the popup tracks live title/status/state changes.
/// </summary>
public interface IBackgroundActivity : INotifyPropertyChanged
{
    /// <summary>Short, already-localized name shown as the row title (e.g. "Reading chips").</summary>
    string Title { get; }

    /// <summary>Live one-line status (e.g. "Watching rentchip.csv every 5 s"). May be empty.</summary>
    string StatusText { get; }

    /// <summary>Current run state, driving the row's icon/label and the pause-vs-resume affordance.</summary>
    BackgroundActivityState State { get; }

    /// <summary>True while paused — lets the popup swap the pause button for a resume one.</summary>
    bool IsPaused { get; }

    /// <summary>True while the activity is running but failing — drives the red state chip and the red
    /// top-bar glyph/badge, so a stalled process is visible without opening the popup.</summary>
    bool HasProblem { get; }

    /// <summary>True only in the plain healthy Running state (neither paused nor failing) — the condition
    /// for the green/accent "Running" chip, which must yield to both other states.</summary>
    bool IsRunningNormally { get; }

    /// <summary>Whether this activity supports pause/resume (hides the button when false).</summary>
    bool CanPause { get; }

    /// <summary>Whether this activity can be stopped from the popup (hides the button when false).</summary>
    bool CanStop { get; }

    /// <summary>Whether the popup can jump to where this activity is configured (hides when false).</summary>
    bool CanOpenSettings { get; }

    /// <summary>Toggles between paused and running (a single button in the popup).</summary>
    IRelayCommand TogglePauseCommand { get; }

    /// <summary>Stops the process entirely; it then leaves the active list.</summary>
    IRelayCommand StopCommand { get; }

    /// <summary>Navigates to the page where this process is configured.</summary>
    IRelayCommand OpenSettingsCommand { get; }
}
