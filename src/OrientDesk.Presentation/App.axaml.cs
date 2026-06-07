using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
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
        var services = PresentationServiceCollectionExtensions.BuildApplicationServices();
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

            // Diagnostics hook: open Settings immediately (used for UI verification).
            if (Environment.GetEnvironmentVariable("ORIENTDESK_OPEN_SETTINGS") == "1")
                mainViewModel.OpenSettingsCommand.Execute(null);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
