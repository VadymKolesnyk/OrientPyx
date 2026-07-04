using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace OrientPyx.Presentation;

/// <summary>
/// Shown once, right after a Velopack install/update, to reassure the user that the app installed
/// correctly: it names the install folder, the (separate) competition-data folder that survives
/// updates, and the shortcuts Velopack created. Built entirely in code (no XAML/DI/localization)
/// in the same self-contained style as <see cref="CrashWindow"/> so it has no startup dependencies.
/// </summary>
internal sealed class WelcomeWindow : Window
{
    public WelcomeWindow(string installDir, string dataDir)
    {
        Title = "OrientPyx — встановлено / installed";
        Width = 620;
        MinWidth = 460;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        CanResize = false;

        var heading = new TextBlock
        {
            Text = "OrientPyx успішно встановлено ✅",
            FontSize = 18,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap
        };

        var subheading = new TextBlock
        {
            Text = "Програму встановлено та готово до роботи. Ось де що знаходиться:",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.Gray
        };

        var installLine = new SelectableTextBlock
        {
            Text = $"📁 Програма встановлена в:\n{installDir}",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13
        };

        var dataLine = new SelectableTextBlock
        {
            Text = $"🏆 Дані змагань зберігаються в:\n{dataDir}\n" +
                   "(ця папка НЕ видаляється під час оновлень — ваші змагання в безпеці)",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13
        };

        var shortcutsLine = new TextBlock
        {
            Text = "🔗 Створено ярлики: на робочому столі та в меню «Пуск».\n" +
                   "🗑 Видалити програму можна через «Параметри → Додатки» або «Панель керування».",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13
        };

        var okButton = new Button
        {
            Content = "Почати роботу",
            HorizontalAlignment = HorizontalAlignment.Right,
            MinWidth = 140
        };
        okButton.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(24),
            Spacing = 14,
            Children = { heading, subheading, installLine, dataLine, shortcutsLine, okButton }
        };
    }
}
