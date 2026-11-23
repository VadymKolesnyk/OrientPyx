using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using OrientPyx.DataAccess.Network.Protocol;
using OrientPyx.Localization;

namespace OrientPyx.Presentation;

/// <summary>
/// The loading window: shown the moment the framework is up, while the databases are prepared, the plugins load and
/// the main window is built behind it (see <c>App</c>), then closed once the main window is on screen — so a double
/// click on the program or on a competition archive answers at once instead of after several silent seconds. Built in
/// code (like <see cref="WelcomeWindow"/>) so it needs nothing but the localization service.
/// </summary>
internal sealed class StartupWindow : Window
{
    private readonly ILocalizationService _localization;
    private readonly TextBlock _status;

    public StartupWindow(ILocalizationService localization)
    {
        _localization = localization;

        Title = localization.Get("App.Title");
        Width = 420;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowDecorations = WindowDecorations.BorderOnly;
        CanResize = false;
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://OrientPyx/Assets/orientpyx.ico")));

        var logo = new Image
        {
            Source = new Bitmap(AssetLoader.Open(new Uri("avares://OrientPyx/Assets/orientpyx.png"))),
            Width = 56,
            Height = 56,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var name = new TextBlock
        {
            Text = localization.Get("App.Title"),
            FontSize = 22,
            FontWeight = FontWeight.SemiBold,
        };

        var version = new TextBlock
        {
            Text = Wire.AppVersion,
            FontSize = 13,
            Foreground = Brushes.Gray,
        };

        _status = new TextBlock
        {
            Text = localization.Get("Common.Loading"),
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var heading = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 16,
            Children =
            {
                logo,
                new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2, Children = { name, version } },
            },
        };

        Content = new StackPanel
        {
            Margin = new Thickness(28, 24),
            Spacing = 18,
            Children =
            {
                heading,
                new StackPanel
                {
                    Spacing = 8,
                    Children = { _status, new ProgressBar { IsIndeterminate = true, MinHeight = 4, Height = 4 } },
                },
            },
        };
    }

    /// <summary>Shows what is being done now (a localization key). UI thread.</summary>
    public void SetStatus(string key) => _status.Text = _localization.Get(key);
}
