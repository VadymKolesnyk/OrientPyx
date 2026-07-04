using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using OrientPyx.Presentation.ViewModels.Pages;

namespace OrientPyx.Presentation.Behaviors;

/// <summary>
/// Attached behaviour that renders a chip-number cell bold + red when the number is NOT in the
/// competition's rental-chip database, so an organiser instantly spots a non-rental chip. It wires onto
/// both the editing <see cref="TextBox"/> and the resting <see cref="TextBlock"/> of a lazy chip cell,
/// re-evaluating whenever the text changes or the shared <see cref="RentalChipRegistry"/> changes (e.g.
/// after a rental toggle) so it always reflects the current database without a page reload.
///
/// This behaviour is highlight-only. Toggling a chip's rental status is a <b>table</b> concern: the
/// chip column is flagged <see cref="Controls.SheetColumn.RentalChipColumn"/> and the table's right-click
/// menu (which owns the rental registry and toggle command) appends the "mark (non-)rental" item to its
/// default filter menu — so there is one cell context menu, not a competing chip-specific one.
/// </summary>
public static class ChipHighlight
{
    /// <summary>The shared rental-chip set the editor checks against. Setting it wires the behaviour.</summary>
    public static readonly AttachedProperty<RentalChipRegistry?> RegistryProperty =
        AvaloniaProperty.RegisterAttached<TextBox, RentalChipRegistry?>("Registry", typeof(ChipHighlight));

    public static void SetRegistry(TextBox box, RentalChipRegistry? value) => box.SetValue(RegistryProperty, value);
    public static RentalChipRegistry? GetRegistry(TextBox box) => box.GetValue(RegistryProperty);

    /// <summary>
    /// The same rental-chip set, but for a read-only <see cref="TextBlock"/> — the resting face of a
    /// lazy chip cell. Bold-reds the label when its text is not a rental chip. Setting it wires the
    /// behaviour.
    /// </summary>
    public static readonly AttachedProperty<RentalChipRegistry?> LabelRegistryProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, RentalChipRegistry?>("LabelRegistry", typeof(ChipHighlight));

    public static void SetLabelRegistry(TextBlock block, RentalChipRegistry? value) => block.SetValue(LabelRegistryProperty, value);
    public static RentalChipRegistry? GetLabelRegistry(TextBlock block) => block.GetValue(LabelRegistryProperty);

    // The brush used for a non-rental chip; bold weight is applied alongside it.
    private static readonly IBrush NonRentalBrush = new SolidColorBrush(Color.FromRgb(0xD3, 0x2F, 0x2F));

    static ChipHighlight()
    {
        RegistryProperty.Changed.AddClassHandler<TextBox>((box, e) => Attach(box, e.NewValue as RentalChipRegistry));
        LabelRegistryProperty.Changed.AddClassHandler<TextBlock>((block, e) => AttachLabel(block, e.NewValue as RentalChipRegistry));
    }

    // ── Resting label ───────────────────────────────────────────────────────────────────────────────
    private static void AttachLabel(TextBlock block, RentalChipRegistry? registry)
    {
        block.PropertyChanged -= OnLabelTextChanged;
        if (block.GetValue(LabelSubscriptionProperty) is { } prev)
            prev.Dispose();
        block.SetValue(LabelSubscriptionProperty, null);

        if (registry is null)
            return;

        block.PropertyChanged += OnLabelTextChanged;
        void OnRegistryChanged(object? _, EventArgs __) => ApplyLabel(block, registry);
        registry.Changed += OnRegistryChanged;
        block.SetValue(LabelSubscriptionProperty, new Subscription(() => registry.Changed -= OnRegistryChanged));

        ApplyLabel(block, registry);
    }

    private static void OnLabelTextChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == TextBlock.TextProperty && sender is TextBlock block)
            ApplyLabel(block, GetLabelRegistry(block));
    }

    private static void ApplyLabel(TextBlock block, RentalChipRegistry? registry)
    {
        var nonRental = registry is not null && registry.IsNonRental(block.Text);
        if (nonRental)
        {
            block.Foreground = NonRentalBrush;
            block.FontWeight = FontWeight.Bold;
        }
        else
        {
            block.ClearValue(TextBlock.ForegroundProperty);
            block.ClearValue(TextBlock.FontWeightProperty);
        }
    }

    private static readonly AttachedProperty<Subscription?> LabelSubscriptionProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, Subscription?>("LabelSubscription", typeof(ChipHighlight));

    // ── Editing TextBox ─────────────────────────────────────────────────────────────────────────────
    private static void Attach(TextBox box, RentalChipRegistry? registry)
    {
        // Detach first so a re-template / re-bind stays idempotent (cells are recycled on rebuild).
        box.PropertyChanged -= OnBoxTextChanged;

        // Drop any previous registry subscription stored on the box.
        if (box.GetValue(SubscriptionProperty) is { } prev)
            prev.Dispose();
        box.SetValue(SubscriptionProperty, null);

        if (registry is null)
            return;

        box.PropertyChanged += OnBoxTextChanged;
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
