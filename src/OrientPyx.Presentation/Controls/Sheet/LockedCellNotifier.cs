using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace OrientPyx.Presentation.Controls;

/// <summary>
/// Makes a plain (non-<see cref="LazyEditCell"/>) cell control explain itself when a closed day holds
/// it read-only.
///
/// A lazy cell raises <see cref="LazyEditCell.LockedEditAttemptedEvent"/> from its own activation path.
/// A checkbox has no such path — it is either enabled or not — and a disabled control receives no input
/// at all, so the click lands on nothing and the user gets no answer. This lays a transparent catcher
/// over the control, visible only while the day is closed, which takes that click and reports it.
/// </summary>
internal static class LockedCellNotifier
{
    private static readonly FuncValueConverter<int, bool> IsLocked = new(day => day > 0);

    /// <summary>
    /// Wraps <paramref name="control"/> so a click, while <paramref name="lockedDayPath"/> resolves to a
    /// non-zero day number, raises the explanation event. Returns the wrapper to place in the cell.
    /// </summary>
    public static Control Wrap(Control control, string lockedDayPath, bool merged = false)
    {
        var catcher = new LockedCatcher(merged)
        {
            Background = Brushes.Transparent,
            [!LockedCatcher.LockedDayProperty] = new Binding(lockedDayPath),
        };
        // Only in the way while the day is closed; on an open day the control underneath behaves exactly
        // as it did before.
        catcher[!Visual.IsVisibleProperty] = new Binding(lockedDayPath) { Converter = IsLocked };

        var panel = new Panel();
        panel.Children.Add(control);
        panel.Children.Add(catcher);
        return panel;
    }

    private sealed class LockedCatcher(bool merged) : Border
    {
        public static readonly StyledProperty<int> LockedDayProperty =
            AvaloniaProperty.Register<LockedCatcher, int>(nameof(LockedDay));

        public int LockedDay
        {
            get => GetValue(LockedDayProperty);
            set => SetValue(LockedDayProperty, value);
        }

        protected override void OnPointerPressed(Avalonia.Input.PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (LockedDay <= 0)
                return;

            e.Handled = true;
            RaiseEvent(new SheetLockedEditEventArgs(
                LazyEditCell.LockedEditAttemptedEvent, LockedDay, merged));
        }
    }
}
