using System.Text;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.DataAccess.Persistence;
using Velopack;

namespace OrientPyx.Presentation;

internal static class Program
{
    // Resolved lazily: the data root can be redirected (installed builds) before the first crash is logged.
    private static string CrashLogPath => Path.Combine(AppDatabasePaths.BaseDirectory, "crash.log");

    /// <summary>
    /// The per-launch activity log, set once DI is built (see <c>App</c>). Crashes are also routed
    /// here so they land in the events-folder log alongside user actions. Null before startup.
    /// </summary>
    internal static IActivityLog? ActivityLog { get; set; }

    /// <summary>
    /// Set true by Velopack's first-run hook (the very first launch after an install/update). The UI
    /// reads this once it is up (see <c>App</c>) to show the one-time <see cref="WelcomeWindow"/>.
    /// </summary>
    internal static bool IsFirstRun { get; private set; }

    // Avalonia configuration; don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called.
    [STAThread]
    public static void Main(string[] args)
    {
        // Velopack install/update/uninstall hooks. MUST run first: on those code paths it does its work
        // and exits the process before the UI is ever built. On a normal launch it returns immediately.
        // OnFirstRun fires on the first launch Velopack triggers right after an install/update — we just
        // record it here (no UI exists yet) and surface the welcome window once the UI is up.
        VelopackApp.Build()
            .OnFirstRun(_ => IsFirstRun = true)
            .Run();

        // Installed builds keep competition data in a stable per-user folder so it survives auto-updates
        // (which replace the application directory wholesale). No-op for an in-place dev/xcopy build.
        RedirectDataRootIfInstalled();

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

    /// <summary>
    /// When running as a Velopack-installed app, redirect data/events/logs to a stable per-user folder
    /// so they survive updates. Velopack lays the app out as <c>&lt;root&gt;\current\OrientPyx.exe</c> with
    /// <c>&lt;root&gt;\Update.exe</c> one level up; the presence of that sibling is our "installed" signal.
    /// A plain build (no sibling Update.exe) is left untouched so <c>dotnet run</c> writes next to the exe.
    ///
    /// CRITICAL: every auto-update replaces the whole <c>current</c> folder, so anything written under it
    /// (competition data, the app DB, logs) is destroyed on the next update. Early installed builds wrote
    /// their <c>events</c>/<c>data</c> straight into <c>current</c>; if that data is still there we rescue
    /// it into <c>data-root</c> before anything opens the databases (see <see cref="RescueDataFromInstallDir"/>).
    /// </summary>
    private static void RedirectDataRootIfInstalled()
    {
        try
        {
            // AppContext.BaseDirectory ends with a trailing separator (...\current\). Directory.GetParent
            // on a trailing-slash path returns that same directory (...\current), NOT its parent, so we must
            // trim the separator first to reach ...\OrientPyx where Velopack's Update.exe lives. Missing this
            // made the "installed" check always fail, leaving data in the update-volatile install folder.
            var appDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var updateExe = Path.Combine(Directory.GetParent(appDir)?.FullName ?? appDir, "Update.exe");
            if (!File.Exists(updateExe))
                return;

            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OrientPyx", "data-root");

            // Rescue any data an earlier build left inside the (update-volatile) install folder BEFORE we
            // point the app at data-root — otherwise the next update wipes it, as happened for existing users.
            RescueDataFromInstallDir(appDir, root);

            AppDatabasePaths.UseDataRoot(root);
        }
        catch
        {
            // Best-effort: if detection fails, fall back to the default (next to the exe).
        }
    }

    /// <summary>
    /// One-time rescue: moves <c>events</c>/<c>data</c> that an earlier installed build wrote inside the
    /// install folder (<paramref name="appDir"/>, i.e. <c>...\current</c>) into the stable
    /// <paramref name="dataRoot"/>. Only runs when data-root does not already have that folder, so it never
    /// clobbers newer data. Best-effort and copy-based (a cross-folder move can fail if a file is locked);
    /// the source is left in place — the next update removes it anyway.
    /// </summary>
    private static void RescueDataFromInstallDir(string appDir, string dataRoot)
    {
        foreach (var folder in new[] { AppDatabasePaths.DefaultEventsFolderName, AppDatabasePaths.DefaultDataFolderName })
        {
            try
            {
                var source = Path.Combine(appDir, folder);
                var destination = Path.Combine(dataRoot, folder);

                // Nothing to rescue, or the destination already exists (data-root wins) — skip.
                if (!Directory.Exists(source) || Directory.Exists(destination))
                    continue;

                // An empty leftover folder (just a stray "logs" subdir, no real data) isn't worth rescuing.
                if (!DirectoryHasFiles(source))
                    continue;

                CopyDirectory(source, destination);
            }
            catch
            {
                // Best-effort per folder: a failure to rescue one must not block startup.
            }
        }
    }

    /// <summary>True if the directory tree contains at least one file (not just empty sub-folders).</summary>
    private static bool DirectoryHasFiles(string path)
    {
        try
        {
            return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Any();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Recursively copies a directory tree. Creates <paramref name="destination"/> if missing.</summary>
    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);

        foreach (var dir in Directory.EnumerateDirectories(source))
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
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

        // Also record it in the events-folder activity log, if it's up yet.
        try
        {
            if (ex is not null)
                ActivityLog?.Error($"CRASH via {source}", ex);
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
