using OrientPyx.BusinessLogic.Services.Text;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.DataAccess.DependencyInjection;
using OrientPyx.DataAccess.Persistence;
using OrientPyx.Localization;
using OrientPyx.Presentation.DependencyInjection;
using OrientPyx.Presentation.Services;
using OrientPyx.Presentation.ViewModels;
using OrientPyx.Presentation.Views;

namespace OrientPyx.Presentation;

public partial class App : Application
{
    public override void Initialize()
    {
        StartupTimeline.Mark("Avalonia platform up");
        AvaloniaXamlLoader.Load(this);
        StartupTimeline.Mark("app styles loaded");
    }

    private UiStallWatchdog? _stallWatchdog;
    private readonly TaskCompletionSource<MainWindow> _mainWindowReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The main window, once start-up has built and shown it — the loading window stands in until then.</summary>
    public Task<MainWindow> MainWindowReady => _mainWindowReady.Task;

    private readonly TaskCompletionSource _startupSettled = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when the startup screen has settled: the last competition restored, or the list shown.</summary>
    public Task StartupSettled => _startupSettled.Task;

    public override void OnFrameworkInitializationCompleted()
    {
        // Catch exceptions raised on the UI thread (e.g. during a page's render/binding):
        // log them and show the crash dialog. Marking them handled keeps the app alive so the
        // dialog can be read, instead of the whole process tearing down.
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Program.HandleCrash("Dispatcher.UIThread.UnhandledException", e.Exception);
            e.Handled = true;
        };

        // Make legacy code pages (windows-1251 etc.) available for participant-file decoding on every
        // platform — .NET ships only UTF/ASCII by default off Windows. Idempotent.
        XmlEncodingReader.EnsureCodePagesRegistered();

        var services = PresentationServiceCollectionExtensions.BuildApplicationServices();

        // Start the per-launch activity log and route global crashes through it.
        var activityLog = services.GetRequiredService<IActivityLog>();
        Program.ActivityLog = activityLog;
        activityLog.Info("Application services built; initializing database.");
        StartupTimeline.Attach(activityLog);
        StartupTimeline.Mark("services built");
        foreach (var note in Program.StartupNotes)
            activityLog.Info(note);

        // Log every UI-thread stall over a second (what Windows shows as «Не відповідає» when it lasts) with the
        // background activities running at that moment — the evidence for the next freeze report.
        _stallWatchdog = new UiStallWatchdog(
            activityLog,
            services.GetRequiredService<IBackgroundActivityService>(),
            services.GetRequiredService<IWorkspaceMode>());

        // Expose the scale service app-wide so any View can bind scaled font sizes
        // ({Binding TitleFontSize, Source={StaticResource UiScale}}).
        Resources["UiScale"] = services.GetRequiredService<IUiScaleService>();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // The slow start-up work (databases, plugins) starts first, off the UI thread; the loading window goes up
            // meanwhile and closes when the main window is on screen (StartAsync). Its status lines are posted, so they
            // run once this method has returned — the window exists by then.
            StartupWindow? splash = null;
            var prepared = Task.Run(() => PrepareData(services, key => Dispatcher.UIThread.Post(() => splash?.SetStatus(key))));
            splash = new StartupWindow(services.GetRequiredService<ILocalizationService>());
            splash.Opened += (_, _) => StartupTimeline.Mark("loading window shown");
            desktop.MainWindow = splash;

            // macOS: Finder delivers a competition archive as an activation (both at launch and into a running app) —
            // the bundle's Info.plist declares the .opyx document type; nothing arrives in args. Subscribed now, so
            // one delivered while the app is still starting waits for the main window instead of being lost.
            if (this.TryGetFeature<IActivatableLifetime>() is { } activatable)
            {
                activatable.Activated += (_, e) =>
                {
                    if (e is not FileActivatedEventArgs files)
                        return;
                    foreach (var path in files.Files.Select(f => f.TryGetLocalPath()).Where(FileAssociation.IsArchivePath))
                        _ = ImportArchiveWhenReadyAsync(path!, restoreIfNotImported: false);
                };
            }

            _ = StartAsync(desktop, services, splash, prepared);
        }
        else
        {
            PrepareData(services);
            PluginStartup.RegisterStrings(services);
        }

        base.OnFrameworkInitializationCompleted();
    }

    // The start-up work that needs no UI: the app database (migrations, seeds) and the plugin assemblies (their
    // switched-on list lives in that database).
    private static void PrepareData(IServiceProvider services, Action<string>? report = null)
    {
        StartupTimeline.Mark("start-up work started");
        report?.Invoke("Startup.Database");
        services.InitializeOrientPyxDatabase();
        StartupTimeline.Mark("app database ready");

        report?.Invoke("Startup.Plugins");
        PluginStartup.Load(services);
        StartupTimeline.Mark("plugins loaded");
    }

    private async Task StartAsync(
        IClassicDesktopStyleApplicationLifetime desktop, IServiceProvider services, StartupWindow splash, Task prepared)
    {
        MainWindow mainWindow;
        MainWindowViewModel mainViewModel;
        try
        {
            // The loading window keeps painting while the databases are opened.
            await prepared;

            // Registering the plugins' strings raises the localization service's change notification — UI thread.
            PluginStartup.RegisterStrings(services);

            // Let the loading window paint its last status before the UI thread builds the main window.
            splash.SetStatus("Startup.Window");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            mainViewModel = services.GetRequiredService<MainWindowViewModel>();
            StartupTimeline.Mark("view models built");
            mainWindow = new MainWindow { DataContext = mainViewModel };
            StartupTimeline.Mark("main window built");
        }
        catch (Exception ex)
        {
            _mainWindowReady.TrySetException(ex);
            _ = _mainWindowReady.Task.Exception; // observed here: the crash dialog below reports it, once
            Program.HandleCrash("Startup", ex);
            splash.Close();
            return;
        }

        var windowOpened = new TaskCompletionSource();
        mainWindow.Opened += (_, _) =>
        {
            StartupTimeline.Mark("main window shown");
            windowOpened.TrySetResult();
        };

        // On the very first launch after a Velopack install, greet the user and confirm
        // where the app and their (update-safe) competition data live. Shown once, non-blocking.
        if (Program.IsFirstRun)
        {
            mainWindow.Opened += (_, _) =>
            {
                var welcome = new WelcomeWindow(
                    AppContext.BaseDirectory,
                    AppDatabasePaths.DefaultEventsPath);
                welcome.Show(mainWindow);
            };
        }

        desktop.MainWindow = mainWindow;
        mainWindow.Show();
        splash.Close();

        // Launched by double-clicking a competition archive (Windows: Explorer passes it as an argument): the archive
        // is what the user wants open, so the last competition is not restored first — that read every competition
        // and opened the previous one only to leave it. If the archive is not imported (cancelled, unreadable), the
        // start goes on as usual.
        var startupArchive = Program.StartupArchivePath;
        var initialized = mainViewModel.InitializeAsync(restoreLastSession: startupArchive is null);
        _started = new Started(mainViewModel, windowOpened.Task, initialized, services.GetRequiredService<IActivityLog>());
        _mainWindowReady.TrySetResult(mainWindow);

        if (startupArchive is not null)
            _ = ImportArchiveWhenReadyAsync(startupArchive, restoreIfNotImported: true);

        // Relaunched by an update («Завантажити й перезапустити»): once the startup screen settled, say it
        // worked and link to what changed in this version.
        if (Program.UpdatedToVersion is { } updatedTo)
            _ = ShowUpdatedWhenReadyAsync(updatedTo);

        // Every launch of an installed build asks the release feed once, quietly: a newer version = a clickable
        // toast, anything else (up to date, offline, a failed check) = nothing on screen. An update — from that
        // toast's dialog or from Settings — first does what closing the window does (pending writes, network).
        var updates = services.GetRequiredService<IUpdateService>();
        updates.BeforeRestart = mainViewModel.PrepareForUpdateRestartAsync;

        try
        {
            await windowOpened.Task;
            await initialized;
        }
        catch (Exception)
        {
            // A failed restore is reported by InitializeAsync itself; the check still runs.
        }
        _startupSettled.TrySetResult();

        // The assistant (MCP): off by default; when the user switched it on, it starts serving now (never earlier — the
        // window and the competition restore come first). It stops with the window.
        var mcp = services.GetRequiredService<BusinessLogic.Interfaces.IMcpServer>();
        services.GetRequiredService<Services.Mcp.McpUi>().Attach(mcp);
        desktop.Exit += (_, _) =>
        {
            try
            {
                // Off the UI thread: its continuations must not wait for the thread this handler blocks.
                Task.Run(() => mcp.StopAsync()).Wait(TimeSpan.FromSeconds(3));
            }
            catch (Exception)
            {
                // closing anyway
            }
        };
        _ = Task.Run(() => mcp.ApplySettingsAsync());

        await services.GetRequiredService<StartupUpdateCheck>().RunAsync();
    }

    // What the post-start flows wait for: the main window shown and the startup screen settled.
    private sealed record Started(MainWindowViewModel ViewModel, Task WindowOpened, Task Initialized, IActivityLog Log);

    private Started? _started;

    private async Task<Started> WhenStartedAsync()
    {
        await _mainWindowReady.Task;
        var started = _started!;
        await started.WindowOpened;
        await started.Initialized;
        return started;
    }

    // Runs the regular import flow on an archive once the window is up and the startup screen settled (its dialogs
    // need a shown owner). restoreIfNotImported: the start skipped restoring the last competition for this archive.
    private async Task ImportArchiveWhenReadyAsync(string archivePath, bool restoreIfNotImported)
    {
        Started? started = null;
        try
        {
            started = await WhenStartedAsync();
            var imported = await started.ViewModel.ImportCompetitionFileAsync(archivePath);
            if (!imported && restoreIfNotImported)
                await started.ViewModel.RestoreLastSessionAsync();
        }
        catch (Exception ex)
        {
            (started?.Log ?? Program.ActivityLog)?.Error("Open archive on launch", ex);
        }
    }

    private async Task ShowUpdatedWhenReadyAsync(string version)
    {
        Started? started = null;
        try
        {
            started = await WhenStartedAsync();
            started.Log.Info($"Started after the update to {version}.");
            await started.ViewModel.ShowAppUpdatedAsync(version);
        }
        catch (Exception ex)
        {
            (started?.Log ?? Program.ActivityLog)?.Error("Show the update notice", ex);
        }
    }
}
