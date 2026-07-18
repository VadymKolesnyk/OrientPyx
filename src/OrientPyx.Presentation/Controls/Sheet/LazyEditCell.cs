using System;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace OrientPyx.Presentation.Controls;

/// <summary>
/// The shared base for every editable sheet cell: it renders as a plain <see cref="TextBlock"/> while
/// resting and materialises a real editor (a <see cref="TextBox"/>, combo, date picker, …) only when
/// the user enters the cell — by focus, click, or a keystroke. Once the editor goes idle (loses focus
/// and any popup closes) the cell drops back to the resting label.
///
/// This is the one place the "display as text, edit on click" behaviour lives, so it is identical for
/// every cell kind and every table that reuses the control. Two wins fall out of it:
/// <list type="bullet">
/// <item>UX: a cell looks like text and turns into its editor the moment it is clicked/typed into.</item>
/// <item>Perf: a virtualized row's realised visual tree is just <see cref="TextBlock"/>s until edited,
/// so a 600-row roster with many editor columns no longer builds a live editor per cell per row.</item>
/// </list>
///
/// Subclasses supply the editor (<see cref="CreateEditor"/>) and say whether entering the cell should
/// also "open" it (e.g. a combo dropping its list) — see <see cref="ShouldOpenOnActivate"/> and
/// <see cref="OpenEditor"/>. The resting label is kept readable by <c>ExtractCellText</c> (Ctrl+C) and
/// by the table's display/edit flow, which both treat a <see cref="TextBlock"/> as the read-only face.
/// </summary>
internal abstract class LazyEditCell : Decorator
{
    private readonly TextBlock _label;
    private Control? _editor;

    // The row-VM value at the moment the editor was materialised, so Escape can restore it (see
    // CancelEdit). Captured via the subclass's EditSourcePath against the current DataContext; null
    // EditSourcePath (or an unreadable path) opts a cell out of revert — Escape just retires it.
    private bool _hasOriginal;
    private object? _originalValue;

    /// <param name="selectedLabelPath">
    /// Binding path to the text shown on the resting label (e.g. <c>SelectedRegion.Label</c> or a plain
    /// value path). Null leaves the label blank until edited.
    /// </param>
    protected LazyEditCell(string? selectedLabelPath)
    {
        Focusable = true;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;

        _label = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(10, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        if (selectedLabelPath is not null)
            _label[!TextBlock.TextProperty] = new Binding(selectedLabelPath);
        Child = _label;
    }

    /// <summary>The resting label, so subclasses can decorate it (placeholder, dim, highlight).</summary>
    protected TextBlock Label => _label;

    /// <summary>The live editor while editing, or null while resting.</summary>
    public Control? Editor => _editor;

    /// <summary>
    /// Enter edit mode: materialise the editor, focus it, and (when <paramref name="open"/>) drop its
    /// list/calendar. The table calls this for Enter/F2/typing on a resting cell, so the lazy cell
    /// behaves like the always-live editors the table used to host.
    /// </summary>
    /// <param name="caretAt">
    /// When the cell was entered by a mouse click, the click point in this cell's coordinate space. A
    /// text editor places its caret near it (Excel/Word behaviour); other editors ignore it. Null for
    /// keyboard entry.
    /// </param>
    public void BeginEdit(bool open, Point? caretAt = null) => Activate(open, caretAt);

    /// <summary>True when this cell type opens a list/calendar on Enter (combo, date) vs. just edits text.</summary>
    public bool OpensOnEnter => ShouldOpenOnActivate(Key.Enter);

    // ── Subclass contract ─────────────────────────────────────────────────────────────────────────
    /// <summary>Builds the real editor, already bound to its value path(s) on the row.</summary>
    protected abstract Control CreateEditor();

    /// <summary>
    /// The single row-VM property path the editor writes back to (the two-way binding's source). Used to
    /// snapshot the value when editing begins so Escape can restore it (see <see cref="CancelEdit"/>).
    /// Return null to opt out of revert — then Escape just leaves edit mode without restoring.
    /// </summary>
    protected abstract string? EditSourcePath { get; }

    /// <summary>
    /// True if entering the cell with this key should also "open" the editor (vs. just focus it):
    /// a combo drops its list, a date picker its calendar. Default: open on Down/Enter/Space, like a
    /// list editor; text editors override to false (they just take focus and the caret).
    /// </summary>
    protected virtual bool ShouldOpenOnActivate(Key key)
        => key is Key.Down or Key.Enter or Key.Space;

    /// <summary>
    /// Open the editor's drop-down/calendar (called after focus when the activation asked to open).
    /// No-op for a plain text editor. <paramref name="editor"/> is the control from
    /// <see cref="CreateEditor"/>.
    /// </summary>
    protected virtual void OpenEditor(Control editor) { }

    /// <summary>True while the editor is mid-interaction so it must not be retired (dropdown open, etc.).</summary>
    protected virtual bool IsEditorBusy(Control editor) => false;

    /// <summary>
    /// Whether a pointer click on an ALREADY-live editor should re-assert the "open" intent. True for
    /// editors that don't toggle themselves on a click (text caret, date picker) so a click reliably
    /// (re)opens them. False for a combo, whose own pointer handling toggles the dropdown — re-asserting
    /// open on top of that toggle makes the dropdown impossible to open (or close) by clicking the cell.
    /// </summary>
    protected virtual bool ReassertsOpenOnClick => true;

    /// <summary>Hook to detach event handlers a subclass wired on the editor in <see cref="CreateEditor"/>.</summary>
    protected virtual void DetachEditor(Control editor) { }

    /// <summary>
    /// Whether the cell may enter edit mode for the current row. Default: always. A subclass with a
    /// per-row "enabled" binding overrides this so a disabled cell (e.g. a scatter group's read-only
    /// course-order cell) stays a resting label on click/focus/typing rather than materialising a
    /// disabled editor.
    /// </summary>
    protected virtual bool CanActivate() => true;

    /// <summary>
    /// Called after a click-activated editor has been focused, with the click point in this cell's
    /// coordinate space. A text editor moves its caret to the nearest character; other editors no-op.
    /// </summary>
    protected virtual void PlaceCaret(Control editor, Point pointInCell) { }

    // ── Activation lifecycle ────────────────────────────────────────────────────────────────────
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        // Container recycling re-points this cell at a new row — drop any live editor so it can't carry
        // the previous row's editing state; the resting label's binding source follows the DataContext.
        if (_editor is not null)
            Retire();
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        Activate(open: false);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        // A click on the cell should open the editor — even if GotFocus already materialised it (focus
        // fires before the press), so always assert the open intent here. Carry the click point so a
        // text editor lands its caret where the user clicked rather than at the start.
        //
        // Exception: when the editor is ALREADY live and the subclass manages its own open/close on a
        // click (a combo toggles its dropdown on pointer press), re-asserting "open" here would fight
        // that toggle — the combo closes itself, we re-open it, leaving the user unable to close (or
        // worse, a net double-toggle that drops the dropdown the instant it opens). Leave the click to
        // the live editor in that case and only assert focus.
        if (Editor is not null && !ReassertsOpenOnClick)
        {
            Activate(open: false);
            return;
        }
        Activate(open: true, caretAt: e.GetPosition(this));
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        // Typing or a navigation/commit key while resting on the cell promotes it to a live editor.
        if (_editor is null && e.Key is not (Key.Tab or Key.Escape))
            Activate(open: ShouldOpenOnActivate(e.Key));
    }

    // Swap the label for a live editor, give it focus, and (optionally) open it. Safe to call when the
    // editor already exists — it just (re)applies focus and the open intent. When the cell was clicked,
    // caretAt is the click point (cell space) so a text editor can land its caret there.
    private void Activate(bool open, Point? caretAt = null)
    {
        // A cell whose per-row "enabled" binding is false never edits — stay the resting label.
        if (_editor is null && !CanActivate())
            return;

        var editor = _editor;
        if (editor is null)
        {
            editor = CreateEditor();
            // Inherit the cell's DataContext so the editor's path bindings resolve against the row VM.
            editor.DataContext = DataContext;
            // Editors carry the app-wide MinHeight=38 from App.axaml; left alone they'd make the row grow
            // taller than its resting label the moment the cell is focused. Drop the floor and let the
            // editor stretch to the cell's existing height so the row keeps the same height while editing.
            editor.MinHeight = 0;
            editor.VerticalAlignment = VerticalAlignment.Stretch;
            editor.LostFocus += OnEditorLostFocus;
            _editor = editor;
            Child = editor;
            CaptureOriginal();
            SetHostEditing(true);
        }
        else if (!open)
        {
            // Already live and no new open intent (e.g. a redundant GotFocus) — nothing to do.
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (_editor is null)
                return;
            if (!_editor.IsFocused && !_editor.IsKeyboardFocusWithin)
                _editor.Focus();
            if (open)
                OpenEditor(_editor);
            if (caretAt is { } point)
                PlaceCaret(_editor, point);
        }, DispatcherPriority.Input);
    }

    /// <summary>
    /// Cancel the current edit: restore the row-VM value to the snapshot taken when editing began and
    /// drop back to the resting label. The table calls this on Escape. No-op when nothing is being edited
    /// or when the cell opted out of revert (null <see cref="EditSourcePath"/> / unreadable snapshot).
    /// </summary>
    public void CancelEdit()
    {
        if (_editor is null)
            return;
        RestoreOriginal();
        Retire();
    }

    // Snapshot the row-VM value the editor is bound to, so Escape can put it back. Best-effort: an
    // unresolvable path (or no DataContext) simply leaves the cell without a revert snapshot.
    private void CaptureOriginal()
    {
        _hasOriginal = false;
        _originalValue = null;
        if (EditSourcePath is not { } path)
            return;
        var (target, property) = ResolveSourceProperty(path);
        if (property is null)
            return;
        _originalValue = property.GetValue(target);
        _hasOriginal = true;
    }

    // Write the snapshot back onto the row VM, reverting whatever the editor changed. The two-way
    // binding pushes the restored value onto the resting label as usual.
    private void RestoreOriginal()
    {
        if (!_hasOriginal || EditSourcePath is not { } path)
            return;
        var (target, property) = ResolveSourceProperty(path);
        if (property is null || !property.CanWrite)
            return;
        try
        {
            property.SetValue(target, _originalValue);
        }
        catch
        {
            // A type-mismatched revert should never crash editing; leave the current value in place.
        }
    }

    // Resolve a (possibly dotted) property path against the current DataContext to its owning object and
    // the leaf PropertyInfo, so the value can be read (capture) or written (restore). Returns a null
    // property when any segment can't be resolved.
    private (object? Target, PropertyInfo? Property) ResolveSourceProperty(string path)
    {
        object? target = DataContext;
        var segments = path.Split('.');
        for (var i = 0; i < segments.Length && target is not null; i++)
        {
            var property = target.GetType().GetProperty(segments[i]);
            if (property is null)
                return (null, null);
            if (i == segments.Length - 1)
                return (target, property);
            target = property.GetValue(target);
        }
        return (null, null);
    }

    /// <summary>Subclasses call this when the editor's own "open" state closes (e.g. dropdown closed).</summary>
    protected void OnEditorIdle() => RetireIfIdle();

    private void OnEditorLostFocus(object? sender, FocusChangedEventArgs e) => RetireIfIdle();

    // Drop back to the resting label once the editor is neither focused nor busy. Deferred so a click
    // that moves focus within the editor (e.g. search box → dropdown) doesn't tear it down mid-edit.
    private void RetireIfIdle()
    {
        if (_editor is null)
            return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_editor is null)
                return;
            if (_editor.IsFocused || _editor.IsKeyboardFocusWithin || IsEditorBusy(_editor))
                return;
            Retire();
        }, DispatcherPriority.Background);
    }

    private void Retire()
    {
        if (_editor is null)
            return;
        _editor.LostFocus -= OnEditorLostFocus;
        DetachEditor(_editor);
        _editor.DataContext = null;
        _editor = null;
        _hasOriginal = false;
        _originalValue = null;
        Child = _label;
        SetHostEditing(false);
    }

    // Reflect the live-editor state onto the hosting SheetCell's :editing pseudo-class so it keeps its
    // accent outline for the whole edit — including while a combo's dropdown popup (which lives outside
    // this cell's visual tree, so keyboard-focus-within can't see it) holds the focus.
    private void SetHostEditing(bool editing)
        => this.FindAncestorOfType<SheetCell>()?.SetEditing(editing);
}
