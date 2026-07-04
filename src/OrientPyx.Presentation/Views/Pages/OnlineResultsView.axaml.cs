using System;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using OrientPyx.Presentation.ViewModels.Pages;

namespace OrientPyx.Presentation.Views.Pages;

public partial class OnlineResultsView : UserControl
{
    private OnlinePreviewTable? _previewTable;

    public OnlineResultsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    // The preview table renders the active day's spectator-table mock-up with drag-reorderable column headers.
    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        _previewTable ??= this.FindControl<Grid>("PreviewTableHost") is { } host
            ? new OnlinePreviewTable(host)
            : null;
        _previewTable?.Bind(DataContext as OnlineResultsViewModel);
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
