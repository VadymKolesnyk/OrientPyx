using System;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;

namespace OrientDesk.Presentation.Views.Pages;

public partial class OnlineResultsView : UserControl
{
    public OnlineResultsView()
    {
        InitializeComponent();
    }

    // Opens a spectator link in the default browser (whole-row button, so a near-miss still works).
    private void OnLinkClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: string url } || string.IsNullOrWhiteSpace(url))
            return;

        var launcher = TopLevel.GetTopLevel(this)?.Launcher;
        if (launcher is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return;

        _ = launcher.LaunchUriAsync(uri);
    }

    // Copies a spectator link to the clipboard.
    private void OnCopyLinkClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: string url } || string.IsNullOrWhiteSpace(url))
            return;

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
            return;

        _ = clipboard.SetTextAsync(url);
    }
}
