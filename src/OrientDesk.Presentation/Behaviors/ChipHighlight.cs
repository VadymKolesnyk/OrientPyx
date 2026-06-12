using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using OrientDesk.Presentation.ViewModels.Pages;

namespace OrientDesk.Presentation.Behaviors;

/// <summary>
/// Attached behaviour for a chip-number <see cref="TextBox"/>: renders the number bold + red when it
/// is NOT in the competition's rental-chip database (so an organiser instantly sees a non-rental
/// chip), and double-clicking the cell toggles that number in the rental database (add when absent,
/// remove when present). The highlight re-evaluates whenever the text changes or the shared
/// <see cref="RentalChipRegistry"/> changes (e.g. after a toggle), so it always reflects the current
/// database without a page reload.
/// </summary>
public static class ChipHighlight
{
    /// <summary>The shared rental-chip set the cell checks against. Setting it wires the behaviour.</summary>
    public static readonly AttachedProperty<RentalChipRegistry?> RegistryProperty =
        AvaloniaProperty.RegisterAttached<TextBox, RentalChipRegistry?>("Registry", typeof(ChipHighlight));

    /// <summary>Invoked on a double-click with the current chip text, to toggle it in the database.</summary>
    public static readonly AttachedProperty<Action<string>?> ToggleProperty =
        AvaloniaProperty.RegisterAttached<TextBox, Action<string>?>("Toggle", typeof(ChipHighlight));

    public static void SetRegistry(TextBox box, RentalChipRegistry? value) => box.SetValue(RegistryProperty, value);
    public static RentalChipRegistry? GetRegistry(TextBox box) => box.GetValue(RegistryProperty);

    public static void SetToggle(TextBox box, Action<string>? value) => box.SetValue(ToggleProperty, value);
    public static Action<string>? GetToggle(TextBox box) => box.GetValue(ToggleProperty);

    // The brush used for a non-rental chip; bold weight is applied alongside it.
    private static readonly IBrush NonRentalBrush = new SolidColorBrush(Color.FromRgb(0xD3, 0x2F, 0x2F));

    static ChipHighlight()
    {
        RegistryProperty.Changed.AddClassHandler<TextBox>((box, e) => Attach(box, e.NewValue as RentalChipRegistry));
    }

    private static void Attach(TextBox box, RentalChipRegistry? registry)
    {
        // Detach first so a re-template / re-bind stays idempotent (cells are recycled on rebuild).
        box.PropertyChanged -= OnBoxTextChanged;
        box.DoubleTapped -= OnDoubleTapped;

        // Drop any previous registry subscription stored on the box.
        if (box.GetValue(SubscriptionProperty) is { } prev)
            prev.Dispose();
        box.SetValue(SubscriptionProperty, null);

        if (registry is null)
            return;

        box.PropertyChanged += OnBoxTextChanged;
        box.DoubleTapped += OnDoubleTapped;

        void OnRegistryChanged(object? _, EventArgs __) => Apply(box, registry);
        registry.Changed += OnRegistryChanged;
        box.SetValue(SubscriptionProperty, new Subscription(() => registry.Changed -= OnRegistryChanged));

        Apply(box, registry);
    }

    private static void OnBoxTextChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == TextBox.TextProperty && sender is TextBox box)
            Apply(box, GetRegistry(box));
    }

    private static void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not TextBox box)
            return;
        var chip = (box.Text ?? string.Empty).Trim();
        if (chip.Length == 0)
            return;
        GetToggle(box)?.Invoke(chip);
        // The toggle mutates the registry, which raises Changed → Apply; no manual refresh needed.
    }

    private static void Apply(TextBox box, RentalChipRegistry? registry)
    {
        var nonRental = registry is not null && registry.IsNonRental(box.Text);
        if (nonRental)
        {
            box.Foreground = NonRentalBrush;
            box.FontWeight = FontWeight.Bold;
        }
        else
        {
            box.ClearValue(TemplatedControl.ForegroundProperty);
            box.ClearValue(TemplatedControl.FontWeightProperty);
        }
    }

    // Stores the registry-unsubscribe action on the box so it can be released when the registry
    // changes or the box is re-attached.
    private static readonly AttachedProperty<Subscription?> SubscriptionProperty =
        AvaloniaProperty.RegisterAttached<TextBox, Subscription?>("Subscription", typeof(ChipHighlight));

    private sealed class Subscription(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
