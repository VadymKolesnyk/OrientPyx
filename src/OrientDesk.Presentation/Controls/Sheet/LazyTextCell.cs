using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using OrientDesk.Localization;
using OrientDesk.Presentation.Behaviors;
using OrientDesk.Presentation.Converters;
using OrientDesk.Presentation.ViewModels.Pages;

namespace OrientDesk.Presentation.Controls;

/// <summary>
/// A <see cref="LazyEditCell"/> whose editor is a <see cref="TextBox"/>: the resting cell shows the
/// value as plain text and turns into a flat in-cell <see cref="TextBox"/> the moment it is clicked or
/// typed into. See <see cref="LazyEditCell"/> for the shared lifecycle.
///
/// This is the shared replacement for the old "always-live, flat-skinned TextBox" text cell. Carrying
/// every option the page builders need (numeric mask, placeholder, enabled/opacity bindings, rental
/// chip highlight) on one cell type is what lets <c>SheetColumnBuilder</c> and <c>RosterCellFactory</c>
/// share one editable text cell instead of each hand-rolling a TextBox.
/// </summary>
internal sealed class LazyTextCell : LazyEditCell
{
    private static readonly BoolToOpacityConverter DimConverter = new();

    private readonly string _editPath;
    private readonly SheetTextOptions _options;

    /// <param name="valuePath">Path read onto the resting label (usually the same as <paramref name="editPath"/>).</param>
    /// <param name="editPath">Two-way path the editing <see cref="TextBox"/> binds to.</param>
    public LazyTextCell(string valuePath, string editPath, SheetTextOptions options)
        : base(valuePath)
    {
        _editPath = editPath;
        _options = options;

        // The resting label mirrors the editor's enabled/dim/rental-highlight so the cell reads the
        // same whether or not it is being edited (a disabled day cell stays dim; a non-rental chip
        // number stays bold-red as text).
        if (options.EnabledPath is { } enabled)
            Label[!IsEnabledProperty] = new Binding(enabled);
        if (options.OpacityPath is { } opacity)
            Label[!OpacityProperty] = new Binding(opacity) { Converter = DimConverter };
        if (options.RentalChips is { } registry)
        {
            // Same registry the editor uses; bold-red the label when the number isn't a rental chip.
            // (Toggling rental status is the table's right-click menu, not the cell — see ChipHighlight.)
            ChipHighlight.SetLabelRegistry(Label, registry);
        }
    }

    protected override Control CreateEditor()
    {
        var box = new TextBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            // Match the resting label's footprint (Padding 10,0) so focusing the cell doesn't add the
            // app TextBox's vertical padding and push the row taller. MinHeight is cleared by the base.
            Padding = new Thickness(10, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            [!TextBox.TextProperty] = new Binding(_editPath)
            {
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = _options.CommitOnLostFocus
                    ? UpdateSourceTrigger.LostFocus
                    : UpdateSourceTrigger.PropertyChanged
            }
        };
        if (_options.Placeholder is { } ph)
            box.PlaceholderText = ph;
        ApplyMask(box, _options.Mask);
        if (_options.EnabledPath is { } enabled)
            box[!IsEnabledProperty] = new Binding(enabled);
        if (_options.OpacityPath is { } opacity)
            box[!OpacityProperty] = new Binding(opacity) { Converter = DimConverter };
        if (_options.RentalChips is { } registry)
            ChipHighlight.SetRegistry(box, registry);
        return box;
    }

    // A text editor takes focus and the caret on entry; it never "opens" anything.
    protected override bool ShouldOpenOnActivate(Key key) => false;

    private static void ApplyMask(TextBox box, SheetColumnBuilder.NumericMask mask)
    {
        switch (mask)
        {
            case SheetColumnBuilder.NumericMask.Digits: NumericInput.SetDigits(box, true); break;
            case SheetColumnBuilder.NumericMask.Integer: NumericInput.SetInteger(box, true); break;
            case SheetColumnBuilder.NumericMask.Decimal: NumericInput.SetDecimal(box, true); break;
            case SheetColumnBuilder.NumericMask.Time: NumericInput.SetTime(box, true); break;
        }
    }
}

/// <summary>The editor options a <see cref="LazyTextCell"/> understands. All optional.</summary>
internal sealed class SheetTextOptions
{
    public SheetColumnBuilder.NumericMask Mask { get; init; } = SheetColumnBuilder.NumericMask.None;
    public string? Placeholder { get; init; }
    public string? EnabledPath { get; init; }
    public string? OpacityPath { get; init; }
    public bool CommitOnLostFocus { get; init; }

    /// <summary>
    /// The rental-chip set used to bold-red a non-rental number on both the editor and the resting
    /// label. Highlight only — toggling rental status is the table's right-click menu (see ChipHighlight).
    /// </summary>
    public RentalChipRegistry? RentalChips { get; init; }
}
