using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Avalonia.Styling;
using OrientPyx.Localization;
using OrientPyx.Presentation.Behaviors;
using OrientPyx.Presentation.Converters;
using OrientPyx.Presentation.ViewModels.Pages;

namespace OrientPyx.Presentation.Controls;

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

        // When a per-row placeholder path is supplied, the resting label falls back to that placeholder
        // (greyed) while the value is blank — so an empty cell reads the inherited value the same way the
        // editor's watermark does. Re-binds the label's Text/Foreground that the base bound to the value.
        if (options.PlaceholderPath is { } placeholderPath)
        {
            Label[!TextBlock.TextProperty] = new MultiBinding
            {
                Converter = new PlaceholderTextConverter(),
                Bindings = { new Binding(valuePath), new Binding(placeholderPath) }
            };
            Label[!TextBlock.ForegroundProperty] = new MultiBinding
            {
                Converter = new PlaceholderForegroundConverter
                {
                    NormalBrush = ResolveBrush("TextPrimary"),
                    PlaceholderBrush = ResolveBrush("TextMuted"),
                },
                Bindings = { new Binding(valuePath), new Binding(placeholderPath) }
            };
        }

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
        // A per-row placeholder path (inherited default) drives the watermark live; else the static one.
        if (_options.PlaceholderPath is { } placeholderPath)
            box[!TextBox.PlaceholderTextProperty] = new Binding(placeholderPath);
        else if (_options.Placeholder is { } ph)
            box.PlaceholderText = ph;
        ApplyMask(box, _options.Mask);
        if (_options.EnabledPath is { } enabled)
            box[!IsEnabledProperty] = new Binding(enabled);
        if (_options.OpacityPath is { } opacity)
            box[!OpacityProperty] = new Binding(opacity) { Converter = DimConverter };
        if (_options.RentalChips is { } registry)
            ChipHighlight.SetRegistry(box, registry);
        // Flatten the box's own border as a LOCAL value once its template runs — a local value beats the
        // theme's :focus / :pointerover style setters (which a plain style selector couldn't reliably
        // override), so the editor shows no ring of its own; the cell's :editing outline is the only one.
        box.TemplateApplied += OnBoxTemplateApplied;
        // Hard block on control characters reaching the text: the Escape key raises a separate TextInput
        // event carrying U+001B, which a handled KeyDown does NOT suppress — so without this the box could
        // still type the ␛ glyph. Runs in tunnel so it beats the TextBox's own TextInput handling.
        box.AddHandler(InputElement.TextInputEvent, OnBoxTextInput, RoutingStrategies.Tunnel);
        return box;
    }

    protected override void DetachEditor(Control editor)
    {
        if (editor is TextBox box)
        {
            box.TemplateApplied -= OnBoxTemplateApplied;
            box.RemoveHandler(InputElement.TextInputEvent, OnBoxTextInput);
        }
    }

    // Reject any input string containing a C0 control character (except tab/newline/carriage return),
    // notably the U+001B ESC that the Escape key emits. Keeps stray control glyphs out of every text cell.
    private static void OnBoxTextInput(object? sender, TextInputEventArgs e)
    {
        var text = e.Text;
        if (string.IsNullOrEmpty(text))
            return;
        foreach (var c in text)
        {
            if (c < 0x20 && c is not ('\t' or '\n' or '\r'))
            {
                e.Handled = true;
                return;
            }
        }
    }

    private static void OnBoxTemplateApplied(object? sender, Avalonia.Controls.Primitives.TemplateAppliedEventArgs e)
    {
        if (e.NameScope.Find<Border>("PART_BorderElement") is { } border)
        {
            border.BorderThickness = new Thickness(0);
            border.BorderBrush = Brushes.Transparent;
        }
    }

    // Resolves a themed brush by resource key from the application resources, for the placeholder
    // label colours. Falls back to null (inherit) when the key isn't found.
    private static IBrush? ResolveBrush(string key)
    {
        var app = Avalonia.Application.Current;
        if (app is not null && app.TryGetResource(key, app.ActualThemeVariant, out var value) && value is IBrush brush)
            return brush;
        return null;
    }

    // A text editor takes focus and the caret on entry; it never "opens" anything.
    protected override bool ShouldOpenOnActivate(Key key) => false;

    // Honour a per-row enabled binding: a disabled cell (e.g. a scatter group's read-only course-order
    // cell, which shows «N варіантів дистанції») must not enter edit mode at all — otherwise the click
    // would materialise a disabled TextBox that steals focus. Read the bound bool off the row VM; a
    // missing/unreadable path leaves the cell editable.
    protected override bool CanActivate()
    {
        if (_options.EnabledPath is not { } path)
            return true;
        var property = DataContext?.GetType().GetProperty(path);
        return property?.GetValue(DataContext) is not bool enabled || enabled;
    }

    // The two-way TextBox binding writes to this path — snapshot it so Escape can revert.
    protected override string? EditSourcePath => _editPath;

    // Land the caret where the user clicked rather than at the start. Hit-test the click point against
    // the editor's text layout: translate it into the TextPresenter, then ask the laid-out line which
    // character sits under that horizontal distance. Best-effort — any miss leaves the default caret.
    protected override void PlaceCaret(Control editor, Point pointInCell)
    {
        if (editor is not TextBox box)
            return;

        var presenter = box.FindDescendantOfType<TextPresenter>();
        if (presenter is null)
            return;

        var inPresenter = this.TranslatePoint(pointInCell, presenter);
        if (inPresenter is not { } p)
            return;

        var line = presenter.TextLayout.TextLines.Count > 0 ? presenter.TextLayout.TextLines[0] : null;
        if (line is null)
            return;

        var hit = line.GetCharacterHitFromDistance(p.X);
        box.CaretIndex = hit.FirstCharacterIndex + hit.TrailingLength;
    }

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

    /// <summary>
    /// Optional binding path to a per-row placeholder value (e.g. an inherited default). When set, it
    /// drives the editor's watermark AND a greyed resting-label fallback shown while the cell's value is
    /// blank. Takes precedence over the static <see cref="Placeholder"/> for the editor watermark.
    /// </summary>
    public string? PlaceholderPath { get; init; }

    public string? EnabledPath { get; init; }
    public string? OpacityPath { get; init; }
    public bool CommitOnLostFocus { get; init; }

    /// <summary>
    /// The rental-chip set used to bold-red a non-rental number on both the editor and the resting
    /// label. Highlight only — toggling rental status is the table's right-click menu (see ChipHighlight).
    /// </summary>
    public RentalChipRegistry? RentalChips { get; init; }
}
