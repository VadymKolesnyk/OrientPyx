using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.VisualTree;
using OrientDesk.Presentation.Behaviors;

namespace OrientDesk.Presentation.Controls;

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
        picker.LayoutUpdated += (_, _) => DumpHeights(picker);
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
        if (e.NameScope.Find<TextBox>("PART_TextBox") is { } box)
        {
            box.MinHeight = 0;
            box.Padding = new Thickness(10, 0);
            box.VerticalAlignment = VerticalAlignment.Stretch;
            box.VerticalContentAlignment = VerticalAlignment.Center;
            // The text box's own template border carries MinHeight=38 too; drop it once its template runs
            // (and now, in case it has already been applied by the time we get here).
            box.TemplateApplied += OnTextBoxTemplateApplied;
            if (box.FindDescendantOfType<Border>() is { } existing)
                existing.MinHeight = 0;
        }

        if (e.NameScope.Find<Button>("PART_Button") is { } button)
        {
            // The glyph button defaults to MinHeight=38 and paints a fixed-24px day-number box; both grow
            // the row. Clear the floor and slim it to a small, height-neutral affordance.
            button.MinHeight = 0;
            button.MinWidth = 0;
            button.Width = 16;
            button.Padding = new Thickness(0);
            button.VerticalAlignment = VerticalAlignment.Center;
        }
    }

    private static void OnTextBoxTemplateApplied(object? sender, TemplateAppliedEventArgs e)
    {
        if (e.NameScope.Find<Border>("PART_BorderElement") is { } border)
            border.MinHeight = 0;
    }

    private static void OnPickerGotFocus(object? sender, FocusChangedEventArgs e)
    {
        // Only redirect when the picker itself took focus, not when focus is already inside its text box
        // or on the calendar drop-down button (let those keep it).
        if (sender is CalendarDatePicker picker && ReferenceEquals(e.Source, picker))
            FocusTextBox(picker);
    }

    private static bool _dumped;
    private static void DumpHeights(Visual root)
    {
        if (_dumped) return;
        _dumped = true;
        foreach (var v in root.GetSelfAndVisualDescendants())
            if (v is Layoutable l)
                System.Console.WriteLine($"[DATECELL] {v.GetType().Name} name={(v as Control)?.Name} H={l.Bounds.Height:F1} MinH={l.MinHeight}");
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
