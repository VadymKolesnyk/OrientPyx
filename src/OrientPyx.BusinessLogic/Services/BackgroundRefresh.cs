namespace OrientPyx.BusinessLogic.Services;

/// <summary>
/// Marks work that refreshes the screen because of a change made elsewhere (another station, the host) — the kind
/// of refresh that must happen silently, like a shared spreadsheet, never behind a «Завантаження» overlay.
/// <para>
/// Two signals, both read by the UI's busy service: <see cref="IsScoped"/> flows with the async call chain of a
/// page's live reload (set by the page base class around <c>ReloadAsync</c>); <see cref="QuietFor"/> opens a short
/// time window for refreshes that reach the pages through an event instead of a call chain (the session refreshing
/// the current day after a remote change raises <c>SessionChanged</c>, which every page answers by reloading).
/// </para>
/// </summary>
public static class BackgroundRefresh
{
    private static readonly AsyncLocal<bool> Scoped = new();
    private static long _quietUntilTicks;

    /// <summary>True inside a live-reload call chain.</summary>
    public static bool IsScoped => Scoped.Value;

    /// <summary>True while a quiet window opened by <see cref="QuietFor"/> is still running.</summary>
    public static bool IsQuiet => DateTime.UtcNow.Ticks < Interlocked.Read(ref _quietUntilTicks);

    /// <summary>True when a refresh started now should stay silent.</summary>
    public static bool IsSilent => IsScoped || IsQuiet;

    /// <summary>Marks the rest of the current async flow as a silent refresh; dispose to end it.</summary>
    public static IDisposable Scope()
    {
        var previous = Scoped.Value;
        Scoped.Value = true;
        return new Restore(previous);
    }

    /// <summary>Opens (or extends) a quiet window: refreshes started before it closes stay silent.</summary>
    public static void QuietFor(TimeSpan duration)
    {
        var until = (DateTime.UtcNow + duration).Ticks;
        long current;
        do
        {
            current = Interlocked.Read(ref _quietUntilTicks);
            if (until <= current)
                return;
        } while (Interlocked.CompareExchange(ref _quietUntilTicks, until, current) != current);
    }

    private sealed class Restore : IDisposable
    {
        private readonly bool _previous;
        public Restore(bool previous) => _previous = previous;
        public void Dispose() => Scoped.Value = _previous;
    }
}
