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

    /// <summary>
    /// Shows a toast with already-localized text. Safe to call from any thread. With <paramref name="onClick"/> the
    /// toast is clickable (a click anywhere but its ✕ closes it and runs that on the UI thread) and stays up for
    /// <see cref="ToastService.ActionLifetime"/>.
    /// </summary>
    void Show(string title, string message, Action? onClick = null);
}

/// <summary>One toast on screen: a heading, a line of detail and the ✕ that closes it early.</summary>
public sealed partial class ToastItem : ObservableObject
{
    private readonly Action<ToastItem> _dismiss;
    private readonly Action? _onClick;

    internal ToastItem(string title, string message, Action? onClick, Action<ToastItem> dismiss)
    {
        Title = title;
        Message = message;
        _onClick = onClick;
        _dismiss = dismiss;
    }

    public string Title { get; }

    public string Message { get; }

    /// <summary>True when a click on the toast does something (the hand cursor).</summary>
    public bool CanOpen => _onClick is not null;

    [RelayCommand]
    private void Dismiss() => _dismiss(this);

    /// <summary>The click on the toast's body: closes it and runs its action.</summary>
    [RelayCommand]
    private void Open()
    {
        if (_onClick is null)
            return;
        _dismiss(this);
        _onClick();
    }
}

/// <inheritdoc />
public sealed class ToastService : IToastService
{
    /// <summary>How long a toast stays up when nobody closes it.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(15);

    /// <summary>How long a clickable toast stays up: it offers something to do, not just news, so it waits longer.</summary>
    public static readonly TimeSpan ActionLifetime = TimeSpan.FromSeconds(60);

    // A burst of reads must not wall off the corner: past this many, the oldest toast makes room.
    private const int MaxVisible = 5;

    public ObservableCollection<ToastItem> Items { get; } = [];

    public void Show(string title, string message, Action? onClick = null)
    {
        if (Dispatcher.UIThread.CheckAccess())
            Add(title, message, onClick);
        else
            Dispatcher.UIThread.Post(() => Add(title, message, onClick));
    }

    private void Add(string title, string message, Action? onClick)
    {
        var toast = new ToastItem(title, message, onClick, Remove);
        Items.Add(toast);
        while (Items.Count > MaxVisible)
            Items.RemoveAt(0);
        DispatcherTimer.RunOnce(() => Remove(toast), onClick is null ? Lifetime : ActionLifetime);
    }

    // Closing an already-gone toast (its timer firing after the ✕, or after it was pushed out) is a no-op.
    private void Remove(ToastItem toast) => Items.Remove(toast);
}
