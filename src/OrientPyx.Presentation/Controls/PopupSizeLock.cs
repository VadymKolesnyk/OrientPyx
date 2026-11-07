using System;
using Avalonia.Layout;
using Avalonia.Threading;

namespace OrientPyx.Presentation.Controls;

/// <summary>
/// Keeps an open popup at the size of its first layout. On macOS a popup is a native window, and one that changes
/// size while open (a list filtered as you type, a mode switch) sometimes keeps showing its previous frame stretched
/// into the new size — the content looks squashed until something repaints. So a popup whose content can change
/// size pins it once open: <see cref="Pin"/> raises the Min sizes to the laid-out size (it can still grow, never
/// shrink), <see cref="Release"/> on close lets the next open size to its content again.
/// </summary>
internal static class PopupSizeLock
{
    /// <summary>Call from the popup's <c>Opened</c>: the popup's first layout has run by then.</summary>
    public static void Pin(Layoutable target, bool width = true, bool height = true)
    {
        if (!TryPin(target, width, height))
            Dispatcher.UIThread.Post(() => TryPin(target, width, height), DispatcherPriority.Background);
    }

    /// <summary>Drops the pinned Min sizes — only for a target that sets no Min size of its own.</summary>
    public static void Release(Layoutable target)
    {
        target.ClearValue(Layoutable.MinWidthProperty);
        target.ClearValue(Layoutable.MinHeightProperty);
    }

    private static bool TryPin(Layoutable target, bool width, bool height)
    {
        var size = target.Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0)
            return false;
        if (width)
            target.MinWidth = Math.Max(target.MinWidth, size.Width);
        if (height)
            target.MinHeight = Math.Max(target.MinHeight, size.Height);
        return true;
    }
}
