using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace OrientDesk.Presentation.Behaviors;

/// <summary>
/// Attached behaviours that restrict what a <see cref="TextBox"/> accepts to a numeric shape,
/// rejecting any keystroke or paste that would make the text invalid. Used by the editable grids
/// (control points, groups) so cells like coordinates, points, distance and time can only ever hold
/// well-formed values — the row view-models still own parsing/formatting; this only blocks bad input.
///
/// The check runs on the <em>resulting</em> text (current text with the edit applied), so partial
/// entries are allowed while typing: an empty box, a lone "-" or ".", "12." etc. all pass and are
/// parsed leniently on save. Three masks are offered via attached properties:
///
/// <list type="bullet">
///   <item><see cref="DigitsProperty"/> — digits only, no sign or separator ("300").</item>
///   <item><see cref="IntegerProperty"/> — optional sign then digits ("-12", "300").</item>
///   <item><see cref="DecimalProperty"/> — optional sign, digits and a single '.' or ',' ("4.5").</item>
///   <item><see cref="TimeProperty"/> — an <c>hh:mm:ss</c> stopwatch mask ("1:30:00").</item>
/// </list>
/// </summary>
public static class NumericInput
{
    public static readonly AttachedProperty<bool> DigitsProperty =
        AvaloniaProperty.RegisterAttached<TextBox, bool>("Digits", typeof(NumericInput));

    public static readonly AttachedProperty<bool> IntegerProperty =
        AvaloniaProperty.RegisterAttached<TextBox, bool>("Integer", typeof(NumericInput));

    public static readonly AttachedProperty<bool> DecimalProperty =
        AvaloniaProperty.RegisterAttached<TextBox, bool>("Decimal", typeof(NumericInput));

    public static readonly AttachedProperty<bool> TimeProperty =
        AvaloniaProperty.RegisterAttached<TextBox, bool>("Time", typeof(NumericInput));

    public static void SetDigits(TextBox box, bool value) => box.SetValue(DigitsProperty, value);
    public static bool GetDigits(TextBox box) => box.GetValue(DigitsProperty);

    public static void SetInteger(TextBox box, bool value) => box.SetValue(IntegerProperty, value);
    public static bool GetInteger(TextBox box) => box.GetValue(IntegerProperty);

    public static void SetDecimal(TextBox box, bool value) => box.SetValue(DecimalProperty, value);
    public static bool GetDecimal(TextBox box) => box.GetValue(DecimalProperty);

    public static void SetTime(TextBox box, bool value) => box.SetValue(TimeProperty, value);
    public static bool GetTime(TextBox box) => box.GetValue(TimeProperty);

    static NumericInput()
    {
        DigitsProperty.Changed.AddClassHandler<TextBox>((box, e) => Toggle(box, (bool)e.NewValue!));
        IntegerProperty.Changed.AddClassHandler<TextBox>((box, e) => Toggle(box, (bool)e.NewValue!));
        DecimalProperty.Changed.AddClassHandler<TextBox>((box, e) => Toggle(box, (bool)e.NewValue!));
        TimeProperty.Changed.AddClassHandler<TextBox>((box, e) => Toggle(box, (bool)e.NewValue!));
    }

    private static void Toggle(TextBox box, bool enabled)
    {
        // Each box uses exactly one mask, so a single subscription is enough regardless of which
        // attached property turned it on. Detaching first keeps re-template/re-bind idempotent.
        box.AddHandler(InputElement.TextInputEvent, OnTextInput, RoutingStrategies.Tunnel, handledEventsToo: true);
        box.RemoveHandler(InputElement.TextInputEvent, OnTextInput);

        if (enabled)
            box.AddHandler(InputElement.TextInputEvent, OnTextInput, RoutingStrategies.Tunnel);
    }

    private static void OnTextInput(object? sender, TextInputEventArgs e)
    {
        if (sender is not TextBox box || string.IsNullOrEmpty(e.Text))
            return;

        var resulting = Project(box, e.Text);
        if (!IsAllowed(box, resulting))
            e.Handled = true;
    }

    // Builds the text the box would hold if this input were accepted, honouring the current
    // selection (typed text replaces the selected span, otherwise it inserts at the caret).
    private static string Project(TextBox box, string input)
    {
        var text = box.Text ?? string.Empty;
        var start = box.SelectionStart;
        var end = box.SelectionEnd;
        if (start > end)
            (start, end) = (end, start);

        start = Math.Clamp(start, 0, text.Length);
        end = Math.Clamp(end, 0, text.Length);

        return string.Concat(text.AsSpan(0, start), input, text.AsSpan(end));
    }

    private static bool IsAllowed(TextBox box, string text)
    {
        if (text.Length == 0)
            return true;

        if (GetDigits(box))
            return IsDigitsShape(text);
        if (GetInteger(box))
            return IsIntegerShape(text);
        if (GetDecimal(box))
            return IsDecimalShape(text);
        if (GetTime(box))
            return IsTimeShape(text);

        return true;
    }

    // Digits only — no sign, no separator. The value stays a string; this just blocks any non-digit
    // character (used by the roster's chip column).
    private static bool IsDigitsShape(string text)
    {
        foreach (var c in text)
            if (!char.IsAsciiDigit(c))
                return false;
        return true;
    }

    // Optional leading '-' followed by zero or more digits. "-" alone is allowed mid-edit.
    private static bool IsIntegerShape(string text)
    {
        var i = 0;
        if (text[0] == '-')
            i = 1;

        for (; i < text.Length; i++)
            if (!char.IsAsciiDigit(text[i]))
                return false;

        return true;
    }

    // Optional sign, digits and at most one decimal separator ('.' or ','). Allows "-", ".", "12.".
    private static bool IsDecimalShape(string text)
    {
        var i = 0;
        if (text[0] == '-')
            i = 1;

        var separators = 0;
        for (; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '.' || c == ',')
            {
                if (++separators > 1)
                    return false;
            }
            else if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    // hh:mm:ss being typed left-to-right: only digits and ':', at most two colons, and each
    // already-complete minute/second group must be < 60. Partial groups (e.g. "1:3") are fine.
    private static bool IsTimeShape(string text)
    {
        var groups = text.Split(':');
        if (groups.Length > 3)
            return false;

        for (var g = 0; g < groups.Length; g++)
        {
            var part = groups[g];
            if (part.Length == 0)
                continue;

            if (part.Length > 2 || !part.All(char.IsAsciiDigit))
                return false;

            // Minutes and seconds (every group after the first) cap at 59 once both digits are in.
            if (g > 0 && part.Length == 2 &&
                int.Parse(part, CultureInfo.InvariantCulture) > 59)
                return false;
        }

        return true;
    }
}
