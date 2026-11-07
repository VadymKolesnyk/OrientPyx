using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;

namespace OrientPyx.Presentation.Controls;

/// <summary>
/// Keeps plain (non-<see cref="LazyEditCell"/>) cell controls in step with their table's
/// <see cref="SheetTable.IsLocked"/>.
///
/// Most editable cells are lazy cells, which consult the lock themselves when they activate. A few are
/// ordinary controls built straight into a column — a boolean column's CheckBox, the trailing delete
/// button — and those would stay live on a closed day.
///
/// Marked controls are tagged when they are built (the builder has no table yet) and set when the table
/// realises the cell (<see cref="ApplyTo"/>), when a row container is prepared, and again on every change of
/// the table's lock (<see cref="SheetTable"/> walks its realised cells). Deliberately <b>no subscription</b> to
/// the table: the table outlives every row it scrolls past, so a binding to it from a cell control kept each
/// discarded row alive — about 1 MB a row, several GB over an hour of scrolling the roster.
/// </summary>
internal static class SheetLock
{
    private enum Mode
    {
        /// <summary>Grey the control out — right for something edited in place (a checkbox).</summary>
        Disable,

        /// <summary>Hide it — right for a row action that makes no sense on a closed day (delete).</summary>
        Hide,
    }

    private static readonly AttachedProperty<Mode?> LockModeProperty =
        AvaloniaProperty.RegisterAttached<SheetTable, Control, Mode?>("LockMode");

    /// <summary>Disables the control while its table is locked (edited-in-place controls).</summary>
    public static void DisableWhenLocked(Control control) => control.SetValue(LockModeProperty, Mode.Disable);

    /// <summary>Hides the control while its table is locked (row actions).</summary>
    public static void HideWhenLocked(Control control) => control.SetValue(LockModeProperty, Mode.Hide);

    /// <summary>Puts every marked control inside a freshly built cell in step with the table's lock.</summary>
    public static void ApplyTo(Control cellContent, SheetTable table) => Apply(cellContent, table.IsLocked);

    /// <summary>Greys out / hides the marked controls under <paramref name="root"/> for the given lock state.</summary>
    public static void Apply(Control root, bool locked)
    {
        foreach (var control in Marked(root))
        {
            var mode = control.GetValue(LockModeProperty)!.Value;
            control.SetValue(mode == Mode.Hide ? Visual.IsVisibleProperty : InputElement.IsEnabledProperty, !locked);
        }
    }

    // The marked control is usually the cell's own root, but a builder may wrap it (a chip editor inside
    // a backdrop panel), so walk the subtree. The LOGICAL tree, not the visual one: this runs as the cell
    // is built, before it is measured, so visual children don't exist yet.
    private static IEnumerable<Control> Marked(Control root)
    {
        if (root.GetValue(LockModeProperty) is not null)
            yield return root;

        foreach (var child in root.GetLogicalDescendants())
        {
            if (child is Control c && c.GetValue(LockModeProperty) is not null)
                yield return c;
        }
    }
}
