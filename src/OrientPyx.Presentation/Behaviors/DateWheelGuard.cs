using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace OrientPyx.Presentation.Behaviors;

/// <summary>
/// Stops the mouse wheel from editing a <see cref="CalendarDatePicker"/>. A hovered/focused picker (and
/// its open calendar) otherwise spins the value or the month under the wheel, so scrolling a page or a
/// sheet past a date field silently rewrites it — the same hazard the sheet already guards against for
/// combo cells (see <c>SheetTable.OnTunnelPointerWheel</c>).
///
/// The handler runs in <see cref="RoutingStrategies.Tunnel"/> so it sees the wheel before the picker's
/// own parts do, marks it handled, and re-raises it on the nearest ancestor <see cref="ScrollViewer"/>
/// outside the picker — so the surrounding page/table still scrolls as the user expects. An open
/// drop-down keeps its own wheel handling (the calendar list needs it).
/// </summary>
internal static class DateWheelGuard
{
    public static void Attach(CalendarDatePicker picker)
    {
        picker.RemoveHandler(InputElement.PointerWheelChangedEvent, OnWheel);
        picker.AddHandler(InputElement.PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
    }

    public static void Detach(CalendarDatePicker picker)
        => picker.RemoveHandler(InputElement.PointerWheelChangedEvent, OnWheel);

    private static void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (sender is not CalendarDatePicker picker || picker.IsDropDownOpen)
            return;

        e.Handled = true;

        // Hand the scroll to whatever would have scrolled had the picker not been in the way.
        if (picker.FindAncestorOfType<ScrollViewer>() is { } scroll)
        {
            var offset = scroll.Offset;
            var delta = e.Delta.Y != 0 ? e.Delta.Y : e.Delta.X;
            const double step = 50;
            scroll.Offset = (e.KeyModifiers & KeyModifiers.Shift) != 0
                ? offset.WithX(offset.X - delta * step)
                : offset.WithY(offset.Y - delta * step);
        }
    }
}
