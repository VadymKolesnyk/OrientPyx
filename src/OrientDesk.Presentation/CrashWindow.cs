using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace OrientDesk.Presentation;

/// <summary>
/// A minimal, dependency-free window shown when an unhandled exception escapes. It tells the
/// user what went wrong and where the crash log lives. Built entirely in code (no XAML, no
/// DI, no localization service) so it can be shown even when the app is in a broken state.
/// </summary>
internal sealed class CrashWindow : Window
{
    public CrashWindow(string source, string? message, string logPath)
    {
        Title = "OrientDesk — помилка / error";
        Width = 640;
        MinWidth = 480;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        CanResize = true;

        var heading = new TextBlock
        {
            Text = "Сталася неочікувана помилка\nAn unexpected error occurred",
            FontSize = 18,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap
        };

        var detail = new SelectableTextBlock
        {
            Text = string.IsNullOrWhiteSpace(message) ? "(no message)" : message,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.Firebrick
        };

        var sourceLine = new TextBlock
        {
            Text = $"Джерело / source: {source}",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.Gray,
            FontSize = 12
        };

        var logLine = new SelectableTextBlock
        {
            Text = $"Повний лог збережено тут / full log saved at:\n{logPath}",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12
        };

        var closeButton = new Button
        {
            Content = "Закрити / Close",
            HorizontalAlignment = HorizontalAlignment.Right,
            MinWidth = 120
        };
        closeButton.Click += (_, _) => Close();

        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(24),
                Spacing = 14,
                Children = { heading, detail, sourceLine, logLine, closeButton }
            }
        };
    }
}
