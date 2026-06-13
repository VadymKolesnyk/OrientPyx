using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using OrientDesk.Localization;
using OrientDesk.Presentation.ViewModels.Pages;

namespace OrientDesk.Presentation.Behaviors;

/// <summary>
/// Attached behaviour for a chip-number <see cref="TextBox"/>: renders the number bold + red when it
/// is NOT in the competition's rental-chip database (so an organiser instantly sees a non-rental
/// chip), and lets the organiser toggle that number in the rental database (add when absent, remove
/// when present) either by <b>Ctrl</b>+double-clicking the cell or via its right-click context menu.
/// The highlight re-evaluates whenever the text changes or the shared <see cref="RentalChipRegistry"/>
/// changes (e.g. after a toggle), so it always reflects the current database without a page reload.
/// </summary>
public static class ChipHighlight
{
    /// <summary>The shared rental-chip set the cell checks against. Setting it wires the behaviour.</summary>
    public static readonly AttachedProperty<RentalChipRegistry?> RegistryProperty =
        AvaloniaProperty.RegisterAttached<TextBox, RentalChipRegistry?>("Registry", typeof(ChipHighlight));

    /// <summary>Invoked with the current chip text to toggle it in the database (Ctrl+double-click or context menu).</summary>
    public static readonly AttachedProperty<Action<string>?> ToggleProperty =
        AvaloniaProperty.RegisterAttached<TextBox, Action<string>?>("Toggle", typeof(ChipHighlight));

    /// <summary>Localization used to label the rental toggle in the cell's context menu.</summary>
    public static readonly AttachedProperty<ILocalizationService?> LocalizationProperty =
        AvaloniaProperty.RegisterAttached<TextBox, ILocalizationService?>("Localization", typeof(ChipHighlight));

    public static void SetRegistry(TextBox box, RentalChipRegistry? value) => box.SetValue(RegistryProperty, value);
    public static RentalChipRegistry? GetRegistry(TextBox box) => box.GetValue(RegistryProperty);

    public static void SetToggle(TextBox box, Action<string>? value) => box.SetValue(ToggleProperty, value);
    public static Action<string>? GetToggle(TextBox box) => box.GetValue(ToggleProperty);

    public static void SetLocalization(TextBox box, ILocalizationService? value) => box.SetValue(LocalizationProperty, value);
    public static ILocalizationService? GetLocalization(TextBox box) => box.GetValue(LocalizationProperty);

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
        box.ContextRequested -= OnContextRequested;

        // Drop any previous registry subscription stored on the box.
        if (box.GetValue(SubscriptionProperty) is { } prev)
            prev.Dispose();
        box.SetValue(SubscriptionProperty, null);

        if (registry is null)
            return;

        box.PropertyChanged += OnBoxTextChanged;
        box.DoubleTapped += OnDoubleTapped;
        box.ContextRequested += OnContextRequested;

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
        // Only a Ctrl+double-click toggles rental — a plain double-click is left to the TextBox for
        // word selection, so the organiser doesn't flip the database while editing the number.
        if (sender is not TextBox box || (e.KeyModifiers & KeyModifiers.Control) == 0)
            return;
        Toggle(box);
    }

    // Right-click → a single context-menu item that toggles the chip's rental status, labelled for the
    // current state (mark as rental when it isn't one, mark as non-rental when it is).
    private static void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not TextBox box)
            return;
        var chip = (box.Text ?? string.Empty).Trim();
        if (chip.Length == 0 || GetToggle(box) is null)
            return;

        var loc = GetLocalization(box);
        var isRental = GetRegistry(box) is { } registry && registry.Contains(chip);
        var label = loc?.Get(isRental ? "Participants.Chip.UnmarkRental" : "Participants.Chip.MarkRental")
            ?? (isRental ? "Mark chip as non-rental" : "Mark chip as rental");

        var item = new MenuItem { Header = label };
        item.Click += (_, _) => Toggle(box);

        var menu = new ContextMenu();
        menu.Items.Add(item);
        menu.Open(box);
        e.Handled = true;
    }

    private static void Toggle(TextBox box)
    {
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
