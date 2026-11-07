using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace OrientPyx.Presentation.Services;

/// <summary>
/// Small non-blocking notices («тости») stacked in the window's bottom-right corner, above every page and dialog.
/// They take no focus and cover nothing but their own corner, so whatever the operator is doing carries on; each
/// one closes by itself after <see cref="ToastService.Lifetime"/>, or on its ✕.
/// </summary>
public interface IToastService
{
    /// <summary>The toasts on screen, oldest first. Touched on the UI thread only.</summary>
    ObservableCollection<ToastItem> Items { get; }

    /// <summary>Shows a toast with already-localized text. Safe to call from any thread.</summary>
    void Show(string title, string message);
}

/// <summary>One toast on screen: a heading, a line of detail and the ✕ that closes it early.</summary>
public sealed partial class ToastItem : ObservableObject
{
    private readonly Action<ToastItem> _dismiss;

    internal ToastItem(string title, string message, Action<ToastItem> dismiss)
    {
        Title = title;
        Message = message;
        _dismiss = dismiss;
    }

    public string Title { get; }

    public string Message { get; }

    [RelayCommand]
    private void Dismiss() => _dismiss(this);
}

/// <inheritdoc />
public sealed class ToastService : IToastService
{
    /// <summary>How long a toast stays up when nobody closes it.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(15);

    // A burst of reads must not wall off the corner: past this many, the oldest toast makes room.
    private const int MaxVisible = 5;

    public ObservableCollection<ToastItem> Items { get; } = [];

    public void Show(string title, string message)
    {
        if (Dispatcher.UIThread.CheckAccess())
            Add(title, message);
        else
            Dispatcher.UIThread.Post(() => Add(title, message));
    }

    private void Add(string title, string message)
    {
        var toast = new ToastItem(title, message, Remove);
        Items.Add(toast);
        while (Items.Count > MaxVisible)
            Items.RemoveAt(0);
        DispatcherTimer.RunOnce(() => Remove(toast), Lifetime);
    }

    // Closing an already-gone toast (its timer firing after the ✕, or after it was pushed out) is a no-op.
    private void Remove(ToastItem toast) => Items.Remove(toast);
}
