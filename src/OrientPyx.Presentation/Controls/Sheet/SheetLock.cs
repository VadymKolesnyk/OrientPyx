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
/// Marked controls are tagged when they are built (the builder has no table yet) and bound when the
/// table realises the cell, in <see cref="ApplyTo"/>. Binding at realisation rather than walking up on
/// attach matters because cells are virtualised and recycled: the walk can run before the cell reaches
/// the tree, and a recycled control would keep a stale subscription.
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

    /// <summary>
    /// Binds every marked control inside a freshly built cell to the table's lock. Called by the table
    /// as it realises each cell, so the binding is re-established on every recycle.
    /// </summary>
    public static void ApplyTo(Control cellContent, SheetTable table)
    {
        foreach (var control in Marked(cellContent))
        {
            var mode = control.GetValue(LockModeProperty)!.Value;
            control.Bind(
                mode == Mode.Hide ? Visual.IsVisibleProperty : InputElement.IsEnabledProperty,
                table.GetObservable(SheetTable.IsLockedProperty, locked => !locked));
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
