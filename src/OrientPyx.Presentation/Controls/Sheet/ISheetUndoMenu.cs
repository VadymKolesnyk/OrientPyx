namespace OrientPyx.Presentation.Controls;

/// <summary>
/// The «Скасувати / Повторити» items a <see cref="SheetTable"/> puts at the top of its cell menu: the headers for the
/// page the table is on (null ⇒ no such item, e.g. nothing to undo or the table is in a dialog) and the actions.
/// Implemented by the shell, which knows the shown page; set once on <see cref="SheetTable.UndoMenu"/>.
/// </summary>
public interface ISheetUndoMenu
{
    string? UndoMenuHeader(SheetTable table);

    string? RedoMenuHeader(SheetTable table);

    void UndoFromMenu(SheetTable table);

    void RedoFromMenu(SheetTable table);
}
