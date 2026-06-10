using System.Text;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace OrientDesk.Presentation;

internal static class Program
{
    private static readonly string CrashLogPath = Path.Combine(AppContext.BaseDirectory, "crash.log");

    // Avalonia configuration; don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called.
    [STAThread]
    public static void Main(string[] args)
    {
        // Capture any crash (background threads, finalizers) to a file and surface it to the
        // user, so UI-only failures aren't lost when there's no console attached.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            HandleCrash("AppDomain.UnhandledException", e.ExceptionObject as Exception);

        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            HandleCrash("UnobservedTaskException", e.Exception);
            e.SetObserved();
        };

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            HandleCrash("Main", ex);
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    /// <summary>
    /// Logs a crash and shows a user-facing crash dialog. Best-effort and re-entrancy-guarded:
    /// it must never throw (a failure here would mask the original crash) and must not loop if
    /// showing the dialog itself crashes.
    /// </summary>
    internal static void HandleCrash(string source, Exception? ex)
    {
        LogCrash(source, ex);

        if (_showingCrashDialog)
            return;
        _showingCrashDialog = true;

        try
        {
            ShowCrashDialog(source, ex?.Message);
        }
        catch
        {
            // Showing the dialog failed; release the guard so the next crash can still try.
            _showingCrashDialog = false;
        }
    }

    private static bool _showingCrashDialog;

    /// <summary>
    /// Appends a full crash report to crash.log next to the executable. Best-effort:
    /// never throws.
    /// </summary>
    private static void LogCrash(string source, Exception? ex)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("==================================================");
            sb.AppendLine($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}] CRASH via {source}");
            sb.AppendLine(ex?.ToString() ?? "(no exception object)");
            sb.AppendLine();

            File.AppendAllText(CrashLogPath, sb.ToString());
        }
        catch
        {
            // swallow — logging must never throw
        }
    }

    /// <summary>
    /// Shows the crash window. If called off the UI thread (background/finalizer crash) it
    /// marshals to the dispatcher when one is still alive; otherwise it does nothing (the file
    /// log is the fallback). Never throws.
    /// </summary>
    private static void ShowCrashDialog(string source, string? message)
    {
        void Show()
        {
            var window = new CrashWindow(source, message, CrashLogPath);
            // Clear the guard once this dialog closes so a later, unrelated crash shows its own
            // dialog instead of being only logged. The guard exists to avoid stacking/looping
            // dialogs while one is already open, not to suppress every crash after the first.
            window.Closed += (_, _) => _showingCrashDialog = false;
            window.Show();
        }

        try
        {
            if (Dispatcher.UIThread.CheckAccess())
                Show();
            else
                Dispatcher.UIThread.Post(Show);
        }
        catch
        {
            // No live dispatcher (e.g. crash before/after the UI loop) — file log remains.
        }
    }
}
