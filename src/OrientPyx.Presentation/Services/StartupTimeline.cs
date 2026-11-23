using System.Diagnostics;
using OrientPyx.BusinessLogic.Interfaces;

namespace OrientPyx.Presentation.Services;

/// <summary>
/// The milestones of one launch, each in milliseconds since the process started («Startup +1234 ms: …»), written to
/// the activity log so a slow start on another computer can be read from that computer's log. Marks made before the
/// log exists are kept and written once <see cref="Attach"/> hands it over. Diagnostics only — never throws.
/// </summary>
internal static class StartupTimeline
{
    private static readonly Stopwatch SinceMain = Stopwatch.StartNew();
    private static readonly object Gate = new();
    private static readonly List<string> Pending = [];
    private static TimeSpan _beforeMain;
    private static IActivityLog? _log;

    /// <summary>Called first thing in <c>Main</c>: remembers how long the runtime took to get there.</summary>
    public static void Start()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            var beforeMain = DateTime.Now - process.StartTime - SinceMain.Elapsed;
            _beforeMain = beforeMain > TimeSpan.Zero ? beforeMain : TimeSpan.Zero;
        }
        catch
        {
            // No process start time on this platform — the marks then count from Main.
        }
        Mark("Main");
    }

    /// <summary>Milliseconds since the process started.</summary>
    public static long ElapsedMs => (long)(_beforeMain + SinceMain.Elapsed).TotalMilliseconds;

    public static void Mark(string milestone)
    {
        var line = $"Startup +{ElapsedMs} ms: {milestone}";
        lock (Gate)
        {
            if (_log is null)
            {
                Pending.Add(line);
                return;
            }
        }
        Write(line);
    }

    private static readonly HashSet<string> Once = [];

    /// <summary>Marks <paramref name="milestone"/> only the first time it is reached in this launch.</summary>
    public static void MarkOnce(string milestone)
    {
        lock (Gate)
        {
            if (!Once.Add(milestone))
                return;
        }
        Mark(milestone);
    }

    /// <summary>Hands over the activity log and writes the marks made before it existed.</summary>
    public static void Attach(IActivityLog log)
    {
        string[] pending;
        lock (Gate)
        {
            _log = log;
            pending = [.. Pending];
            Pending.Clear();
        }
        foreach (var line in pending)
            Write(line);
    }

    private static void Write(string line)
    {
        try
        {
            _log?.Info(line);
        }
        catch
        {
            // Diagnostics must never break the start.
        }
    }
}
