using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using OrientPyx.Presentation.Behaviors;

namespace OrientPyx.Presentation.Controls;

/// <summary>
/// A <see cref="LazyEditCell"/> whose editor is a <see cref="CalendarDatePicker"/>: the resting cell
/// shows the date as <c>dd.MM.yyyy</c> text and, when entered, materialises the picker focused into its
/// text box (dd.MM.yyyy typing) rather than dropping the calendar — so it edits 1:1 like a text cell.
/// See <see cref="LazyEditCell"/> for the shared lifecycle.
///
/// The picker's templated chrome (the inner <c>PART_TextBox</c>, that box's <c>PART_BorderElement</c>,
/// and the <c>PART_Button</c> calendar glyph) all carry the app-wide <c>MinHeight=38</c> from the global
/// TextBox/Button styles, which is taller than a sheet row and grows it the moment the cell is entered.
/// Style selectors of the form <c>SheetCell CalendarDatePicker /template/ …</c> do NOT reach these parts
/// (they live two template levels deep and the descendant+/template/ chain doesn't traverse into them —
/// verified by dumping the live visual tree). So we flatten them <b>directly in code</b> once the picker
/// applies its template, which is reliable. This is what keeps the editor exactly the cell's height.
/// </summary>
internal sealed class LazyDateCell : LazyEditCell
{
    // Formats the bound DateTimeOffset? for the resting label; the editor uses the global
    // DateTimeOffset↔DateTime converter for two-way editing.
    private static readonly FuncValueConverter<DateTimeOffset?, string> FormatDate =
        new(d => d?.ToString("dd.MM.yyyy") ?? string.Empty);

    private readonly string _path;
    private readonly string? _placeholder;

    public LazyDateCell(string path, string? placeholder) : base(selectedLabelPath: null)
    {
        _path = path;
        _placeholder = placeholder;
        Label[!TextBlock.TextProperty] = new Binding(path) { Converter = FormatDate };
    }

    protected override Control CreateEditor()
    {
        var picker = new CalendarDatePicker
        {
            SelectedDateFormat = CalendarDatePickerFormat.Custom,
            CustomDateFormatString = "dd.MM.yyyy",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            // The global CalendarDatePicker style sets MinHeight=38; clear it so the picker can shrink to
            // the row. Padding=0 here (the inner text box gets the 10px inset directly, below).
            MinHeight = 0,
            Padding = new Thickness(0),
            // Hand cursor across the whole picker — the calendar opens from anywhere in the cell, not
            // just the glyph button, so the cursor should read as clickable over the entire area.
            Cursor = new Cursor(StandardCursorType.Hand),
            [!CalendarDatePicker.SelectedDateProperty] = new Binding(_path)
            {
                Mode = BindingMode.TwoWay,
                Converter = Application.Current!.Resources["DateTimeOffsetToDateTime"] as IValueConverter
            }
        };
        if (_placeholder is not null)
            picker.PlaceholderText = _placeholder;
        NumericInput.SetDate(picker, true);
        // The picker is a focus host; whenever it gains focus (click, Tab, keyboard) push the caret into
        // its inner text box so the cell edits as text rather than parking focus on the calendar button.
        picker.GotFocus += OnPickerGotFocus;
        // Flatten the templated parts the moment they exist (and again if the template is re-applied).
        picker.TemplateApplied += OnPickerTemplateApplied;
        return picker;
    }

    protected override void DetachEditor(Control editor)
    {
        if (editor is CalendarDatePicker picker)
        {
            picker.GotFocus -= OnPickerGotFocus;
            picker.TemplateApplied -= OnPickerTemplateApplied;
        }
    }

    // Entering the cell should land in the text input (dd.MM.yyyy editing), not drop the calendar —
    // so the editor opens like a plain text cell. The calendar is still reachable via its button.
    protected override bool ShouldOpenOnActivate(Key key) => false;

    // The picker's SelectedDate two-way binding writes to this path — snapshot it so Escape can revert.
    protected override string? EditSourcePath => _path;

    protected override void OpenEditor(Control editor)
    {
        if (editor is CalendarDatePicker picker)
            FocusTextBox(picker);
    }

    protected override bool IsEditorBusy(Control editor)
        => editor is CalendarDatePicker { IsDropDownOpen: true };

    // Flatten the picker's templated chrome so it measures exactly like a flat in-cell text box.
    private static void OnPickerTemplateApplied(object? sender, TemplateAppliedEventArgs e)
    {
        // The picker's outer chrome is a Border named "Background" (NOT PART_BorderElement, which is the
        // inner text box's border); it carries the ring/fill that reacts to focus-within/pointerover.
        // Flatten it as a local value so no ring shows around the whole picker — the cell's :editing
        // outline is the only edit indicator.
        if (e.NameScope.Find<Border>("Background") is { } pickerBorder)
        {
            pickerBorder.BorderThickness = new Thickness(0);
            pickerBorder.BorderBrush = Brushes.Transparent;
            pickerBorder.Background = Brushes.Transparent;
        }

        if (e.NameScope.Find<TextBox>("PART_TextBox") is { } box)
        {
            box.MinHeight = 0;
            box.Padding = new Thickness(10, 0);
            box.VerticalAlignment = VerticalAlignment.Stretch;
            box.VerticalContentAlignment = VerticalAlignment.Center;
            // The text box would otherwise show an I-beam over its area; keep the hand so the whole
            // picker reads as clickable (the calendar opens on click anywhere).
            box.Cursor = new Cursor(StandardCursorType.Hand);
            // The text box's own template border carries MinHeight=38 too; drop it once its template runs
            // (and now, in case it has already been applied by the time we get here).
            box.TemplateApplied += OnTextBoxTemplateApplied;
            if (box.FindDescendantOfType<Border>() is { } existing)
                existing.MinHeight = 0;
        }

        if (e.NameScope.Find<Button>("PART_Button") is { } button)
        {
            // The glyph button defaults to MinHeight=38 and its OWN template paints a fixed calendar box —
            // two chrome borders, a "current day" number TextBlock, and an Ellipse dot (verified in the
            // live visual tree). Setting Content alone is ignored (that template has no ContentPresenter),
            // so the day number stays. Replace the whole template with a minimal one that shows just a
            // small calendar icon; also clear the height floor and slim the button so the row stays flat.
            button.MinHeight = 0;
            button.MinWidth = 0;
            button.Width = 20;
            button.Padding = new Thickness(0);
            button.VerticalAlignment = VerticalAlignment.Stretch;
            button.HorizontalAlignment = HorizontalAlignment.Center;
            button.Background = Brushes.Transparent;
            button.BorderThickness = new Thickness(0);
            button.Template = new FuncControlTemplate<Button>((_, _) => new Icon
            {
                Kind = "Calendar",
                Size = 14,
                Foreground = Application.Current!.Resources["TextSecondary"] as IBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
    }

    private static void OnTextBoxTemplateApplied(object? sender, TemplateAppliedEventArgs e)
    {
        // Flatten the inner text box's border as a LOCAL value (highest precedence — beats the theme's
        // :focus / :pointerover style setters, which is why a plain style selector couldn't reliably kill
        // the focus ring). No thickness, transparent brush ⇒ no second ring inside the cell's outline.
        if (e.NameScope.Find<Border>("PART_BorderElement") is { } border)
        {
            border.MinHeight = 0;
            border.BorderThickness = new Thickness(0);
            border.BorderBrush = Brushes.Transparent;
        }
    }

    private static void OnPickerGotFocus(object? sender, FocusChangedEventArgs e)
    {
        // Only redirect when the picker itself took focus, not when focus is already inside its text box
        // or on the calendar drop-down button (let those keep it).
        if (sender is CalendarDatePicker picker && ReferenceEquals(e.Source, picker))
            FocusTextBox(picker);
    }

    // Move focus/caret into the picker's inner text box so the cell behaves like a text editor.
    private static void FocusTextBox(CalendarDatePicker picker)
    {
        if (picker.FindDescendantOfType<TextBox>() is not { } box)
            return;
        box.Focus();
        box.CaretIndex = box.Text?.Length ?? 0;
    }
}
