using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace OrientPyx.Presentation.Behaviors;

/// <summary>
/// Attached behaviour that moves keyboard focus onto a control the moment it appears on screen —
/// used on every modal dialog's primary action (the button a user presses most, e.g. «Присвоїти» in
/// the number assignment, «Друк на A4» in the statement) or its main input, so Enter / Space / Tab
/// work right away without a click. Focus is requested with the Tab navigation method so the focus
/// ring (:focus-visible) is drawn, and posted at input priority so it runs after the overlay has
/// finished laying the dialog out (a synchronous Focus() during attach is silently dropped).
/// </summary>
public static class AutoFocus
{
    /// <summary>True focuses the control each time it is attached to the visual tree.</summary>
    public static readonly AttachedProperty<bool> OnOpenProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("OnOpen", typeof(AutoFocus));

    public static void SetOnOpen(Control control, bool value) => control.SetValue(OnOpenProperty, value);
    public static bool GetOnOpen(Control control) => control.GetValue(OnOpenProperty);

    static AutoFocus()
    {
        OnOpenProperty.Changed.AddClassHandler<Control>((control, e) =>
        {
            if (e.NewValue is true)
                control.AttachedToVisualTree += OnAttached;
            else
                control.AttachedToVisualTree -= OnAttached;
        });
    }

    private static void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is not Control control)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            // The dialog may already have been closed, or the control hidden (a state-dependent button).
            if (control.IsEffectivelyVisible && control.IsEffectivelyEnabled)
                control.Focus(NavigationMethod.Tab);
        }, DispatcherPriority.Input);
    }
}
