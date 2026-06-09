using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace OrientDesk.Presentation.Controls;

/// <summary>
/// A reusable, spreadsheet-like <see cref="DataGrid"/> for OrientDesk's editable tables
/// (control points, days, and future participants/groups/courses screens). It bakes in the
/// behaviour those screens share so each View only declares its columns:
///
/// <list type="bullet">
///   <item>click a column header to sort by it (<see cref="DataGrid.CanUserSortColumns"/>);</item>
///   <item>drag column headers to reorder them, with a visible drop-location preview
///         (<see cref="DataGrid.CanUserReorderColumns"/> + the <c>Sheet.axaml</c> indicator skin);</item>
///   <item>columns sized <c>Width="Auto"</c> hug their content but are capped to
///         <see cref="DefaultMaxColumnWidth"/> (200px) at first layout, so one long value doesn't
///         blow a column out — yet the user can still drag it wider afterwards;</item>
///   <item>a horizontal scrollbar appears automatically when the columns are wider than the
///         viewport (e.g. after dragging a column wider or narrowing the window);</item>
///   <item>focusing a cell merely <b>selects</b> it (Excel-style). Editing starts only when the
///         user actually types a character, presses Enter/F2, or pastes with Ctrl+V — for every
///         editor type, not just plain text boxes.</item>
/// </list>
///
/// The compact visual skin (gridlines, row height, borderless in-cell editors, drop indicator)
/// still lives in <c>Styles/Sheet.axaml</c> under the <c>Classes="sheet"</c> selector, which this
/// control sets by default. Column headers are localized from code-behind as before — see the
/// <c>datagrid-sheet-convention</c> note.
/// </summary>
public class SheetDataGrid : DataGrid
{
    // Avalonia resolves a control's template/theme by its concrete type. A bare subclass would
    // have no DataGrid theme and render nothing, so point the style key back at DataGrid.
    protected override Type StyleKeyOverride => typeof(DataGrid);

    // DataGrid doesn't expose its editing state, so we track it from the edit events. The paste
    // handler uses it to avoid re-opening an editor that is already active.
    private bool _isEditing;

    // Auto-width columns are clamped to this on first layout (see OnLayoutUpdated). It's only a
    // default starting cap — the user may drag a column wider afterwards.
    public double DefaultMaxColumnWidth { get; set; } = 200;

    private bool _autoWidthsCapped;

    public SheetDataGrid()
    {
        Classes.Add("sheet");

        // Spreadsheet defaults; a consuming View may still override any of these in XAML.
        CanUserSortColumns = true;
        CanUserReorderColumns = true;
        CanUserResizeColumns = true;

        // Show a horizontal scrollbar when the columns together are wider than the viewport — e.g.
        // after the user drags a column wider than its content, or the window is narrowed.
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;

        // Once the auto-sized columns have a real width, cap the over-wide ones to the default max.
        LayoutUpdated += OnLayoutUpdated;

        BeginningEdit += (_, _) => _isEditing = true;
        CellEditEnded += (_, _) => _isEditing = false;

        // Excel-style edit triggers. We drive editing ourselves (the grid's own keyboard handling
        // would otherwise treat Enter as "move down" and never starts edit on a plain keystroke).
        // Handled in the tunnel phase so we run before the grid's navigation logic.
        AddHandler(KeyDownEvent, OnTunnelKeyDown, RoutingStrategies.Tunnel);
    }

    // Turn each Auto column that auto-sized beyond the cap into a fixed-width column at the cap.
    // Done once, after the first real layout: this gives "size to content, but no wider than 200px
    // by default" while leaving the column freely resizable past 200px from then on. Columns the
    // View gave an explicit fixed/star width are left alone.
    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (_autoWidthsCapped || Columns.Count == 0)
            return;

        // Wait until the grid has actually been measured (ActualWidth flows in after first layout).
        if (Bounds.Width <= 0)
            return;

        foreach (var column in Columns)
        {
            if (column.Width.IsAuto && column.ActualWidth > DefaultMaxColumnWidth)
                column.Width = new DataGridLength(DefaultMaxColumnWidth);
        }

        _autoWidthsCapped = true;
        // Stop reacting to every layout pass once we've applied the one-time cap.
        LayoutUpdated -= OnLayoutUpdated;
    }

    private void OnTunnelKeyDown(object? sender, KeyEventArgs e)
    {
        // Nothing selected, or already editing (let the active editor handle the key itself).
        if (CurrentColumn is null || _isEditing)
            return;

        var ctrlOrAlt = e.KeyModifiers.HasFlag(KeyModifiers.Control) ||
                        e.KeyModifiers.HasFlag(KeyModifiers.Alt);

        // Ctrl+V: open the editor and paste the clipboard text into it.
        if (e.Key == Key.V && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (BeginEdit())
            {
                e.Handled = true;
                Dispatcher.UIThread.Post(() => PasteIntoEditor(), DispatcherPriority.Background);
            }
            return;
        }

        // Enter / F2: open the editor for the selected cell. Always swallow the key so Enter edits
        // the current cell instead of moving the selection down a row (the grid's default). For an
        // always-interactive cell (ComboBox / date picker) BeginEdit is a no-op, but we still stop
        // the row-move so focus stays put.
        if (e.Key is Key.Enter or Key.F2)
        {
            BeginEdit();
            e.Handled = true;
            return;
        }

        // A printable character (letter, digit, symbol, space) with no Ctrl/Alt: start editing and
        // seed the editor with that character — exactly like typing into a spreadsheet cell.
        // KeySymbol is null for navigation/function keys, so those keep navigating the grid.
        if (!ctrlOrAlt && !string.IsNullOrEmpty(e.KeySymbol))
        {
            var symbol = e.KeySymbol;
            if (BeginEdit())
            {
                e.Handled = true;
                Dispatcher.UIThread.Post(() => SeedEditor(symbol), DispatcherPriority.Background);
            }
        }
    }

    // Replace the freshly-opened editor's text with the first typed character and put the caret
    // after it, so the user can keep typing seamlessly.
    private void SeedEditor(string text)
    {
        if (FocusedEditor() is { } box)
        {
            box.Text = text;
            box.CaretIndex = text.Length;
        }
    }

    private async void PasteIntoEditor()
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard || FocusedEditor() is not { } box)
            return;

        var text = await clipboard.TryGetTextAsync();
        if (!string.IsNullOrEmpty(text))
        {
            box.Text = text;
            box.CaretIndex = text.Length;
        }
    }

    private TextBox? FocusedEditor() =>
        TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as TextBox;
}
