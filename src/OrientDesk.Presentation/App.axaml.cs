using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using OrientDesk.BusinessLogic.Interfaces;
using OrientDesk.DataAccess.DependencyInjection;
using OrientDesk.Presentation.DependencyInjection;
using OrientDesk.Presentation.Services;
using OrientDesk.Presentation.ViewModels;
using OrientDesk.Presentation.Views;

namespace OrientDesk.Presentation;

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

        services.InitializeOrientDeskDatabase();

        // Expose the scale service app-wide so any View can bind scaled font sizes
        // ({Binding TitleFontSize, Source={StaticResource UiScale}}).
        Resources["UiScale"] = services.GetRequiredService<IUiScaleService>();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainViewModel = services.GetRequiredService<MainWindowViewModel>();
            desktop.MainWindow = new MainWindow { DataContext = mainViewModel };

            // Restore the last session (or show the picker) once the UI is up.
            _ = mainViewModel.InitializeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
