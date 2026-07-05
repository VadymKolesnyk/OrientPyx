using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using OrientPyx.Presentation.Behaviors;

namespace OrientPyx.Presentation.Controls;

/// <summary>
/// A <see cref="CalendarDatePicker"/> for form fields (the create-competition and competition-info
/// screens) that behaves like the sheet's date cells: it auto-inserts the <c>.</c> separators while
/// typing <c>dd.MM.yyyy</c> (via <see cref="NumericInput.DateProperty"/>) and, on focus, lands the caret
/// in the inner text box so the field edits as text rather than parking focus on the calendar button.
///
/// Unlike the in-cell <c>LazyDateCell</c>, this keeps the picker's normal chrome — the focus ring/border
/// around the field is intentionally left intact. The clean calendar glyph (instead of the boxed day
/// number) comes from the app-wide <c>OD_CalendarDatePickerButton</c> theme, so this control only adds
/// the typing behaviour.
/// </summary>
public sealed class DatePickerBox : CalendarDatePicker
{
    protected override Type StyleKeyOverride => typeof(CalendarDatePicker);

    public DatePickerBox()
    {
        NumericInput.SetDate(this, true);
        GotFocus += OnGotFocus;
    }

    // When the picker itself takes focus (Tab, click on the field), redirect the caret into its inner
    // text box so the user can type the date straight away — matching the sheet date cell.
    private void OnGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, this))
            return;
        if (this.FindDescendantOfType<TextBox>() is not { } box)
            return;
        box.Focus();
        box.CaretIndex = box.Text?.Length ?? 0;
    }
}
