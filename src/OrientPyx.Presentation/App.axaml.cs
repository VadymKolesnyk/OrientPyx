using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.DataAccess.DependencyInjection;
using OrientPyx.DataAccess.Persistence;
using OrientPyx.Presentation.DependencyInjection;
using OrientPyx.Presentation.Services;
using OrientPyx.Presentation.ViewModels;
using OrientPyx.Presentation.Views;

namespace OrientPyx.Presentation;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

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

        services.InitializeOrientPyxDatabase();

        // Load the plugins (needs the migrated app DB for the disabled list) and register their strings —
        // DataAccess loads them but does not know the localization service.
        PluginStartup.RegisterStrings(services);

        // Expose the scale service app-wide so any View can bind scaled font sizes
        // ({Binding TitleFontSize, Source={StaticResource UiScale}}).
        Resources["UiScale"] = services.GetRequiredService<IUiScaleService>();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainViewModel = services.GetRequiredService<MainWindowViewModel>();
            var mainWindow = new MainWindow { DataContext = mainViewModel };
            desktop.MainWindow = mainWindow;

            // Restore the last session (or show the picker) once the UI is up.
            var initialized = mainViewModel.InitializeAsync();

            // Launched by double-clicking a competition archive: once the window is up and the startup
            // screen settled, run the regular import flow on that file (its dialogs need a shown owner).
            var windowOpened = new TaskCompletionSource();
            mainWindow.Opened += (_, _) => windowOpened.TrySetResult();
            var log = services.GetRequiredService<IActivityLog>();

            async Task ImportArchiveWhenReadyAsync(string archivePath)
            {
                try
                {
                    await windowOpened.Task;
                    await initialized;
                    await mainViewModel.ImportCompetitionFileAsync(archivePath);
                }
                catch (Exception ex)
                {
                    log.Error("Open archive on launch", ex);
                }
            }

            // Windows: Explorer passes the file as a command-line argument.
            if (Program.StartupArchivePath is { } startupArchive)
                _ = ImportArchiveWhenReadyAsync(startupArchive);

            // macOS: Finder delivers the file as an activation (both at launch and into a running app) — the
            // bundle's Info.plist declares the .opyx document type; nothing arrives in args.
            if (this.TryGetFeature<IActivatableLifetime>() is { } activatable)
            {
                activatable.Activated += (_, e) =>
                {
                    if (e is not FileActivatedEventArgs files)
                        return;
                    foreach (var path in files.Files.Select(f => f.TryGetLocalPath()).Where(FileAssociation.IsArchivePath))
                        _ = ImportArchiveWhenReadyAsync(path!);
                };
            }

            // On the very first launch after a Velopack install/update, greet the user and confirm
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
        }

        base.OnFrameworkInitializationCompleted();
    }
}
