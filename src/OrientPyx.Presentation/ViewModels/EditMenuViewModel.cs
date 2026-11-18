using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.Localization;
using OrientPyx.Presentation.Controls;
using OrientPyx.Presentation.Services;
using OrientPyx.Presentation.Services.Undo;
using OrientPyx.Presentation.ViewModels.Pages;
using OrientPyx.Presentation.ViewModels.Shared;

namespace OrientPyx.Presentation.ViewModels;

/// <summary>
/// The «Правка» menu and the undo keys of the shell: «Скасувати: …» / «Повторити: …» for the shown page, the same step
/// Ctrl+Z / Ctrl+Y (and the tables' cell menus) act on. Nothing happens while a dialog or overlay is open, on a station
/// without a connection, or on a page without undo (the dashboard, the protocols, the finish page).
/// </summary>
public sealed partial class EditMenuViewModel : ObservableObject, ISheetUndoMenu
{
    private readonly IUndoHistory _undo;
    private readonly ShellViewModel _shell;
    private readonly IDialogService _dialogs;
    private readonly NetworkNoticeViewModel _network;
    private readonly ILocalizationService _localization;

    public EditMenuViewModel(
        IUndoHistory undo,
        ShellViewModel shell,
        IDialogService dialogs,
        NetworkNoticeViewModel network,
        ILocalizationService localization)
    {
        _undo = undo;
        _shell = shell;
        _dialogs = dialogs;
        _network = network;
        _localization = localization;

        undo.Changed += (_, _) => Refresh();
        shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShellViewModel.SelectedPage))
            {
                WatchPage(_shell.SelectedPage);
                Refresh();
            }
        };
        WatchPage(_shell.SelectedPage);
        localization.PropertyChanged += (_, _) => Refresh();
        network.PropertyChanged += (_, _) => Refresh();
        // The tables' cell menus show the same two items (see SheetTable.UndoMenu).
        SheetTable.UndoMenu = this;
        Refresh();
    }

    // The shown page: its own day switch (its picker) changes which history the items show, so any change of it
    // re-reads them — cheap, it only sets properties. Pages are singletons; only the shown one is watched.
    private PageViewModelBase? _watched;

    private void WatchPage(object? page)
    {
        if (_watched is not null)
            _watched.PropertyChanged -= OnPagePropertyChanged;
        _watched = page as PageViewModelBase;
        if (_watched is not null)
            _watched.PropertyChanged += OnPagePropertyChanged;
    }

    // The histories are UI-thread state: a page that raises a change from elsewhere is answered on the UI thread.
    private void OnPagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            Refresh();
        else
            Avalonia.Threading.Dispatcher.UIThread.Post(Refresh);
    }

    /// <summary>Set by the root view model: true while the working shell (not a selection screen or overlay) is shown.</summary>
    public Func<bool> IsShellShown { get; set; } = () => true;

    [ObservableProperty]
    private string _undoHeader = string.Empty;

    [ObservableProperty]
    private string _redoHeader = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand))]
    private bool _canUndo;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RedoCommand))]
    private bool _canRedo;

    private PageViewModelBase? Page => _shell.SelectedPage is { SupportsUndo: true } page ? page : null;

    // Undo acts only on the shown page, with no dialog over it and (on a station) a live connection.
    private bool Available => Page is not null && IsShellShown() && !_dialogs.IsOpen && !_network.IsOffline;

    /// <summary>Re-reads the labels (also when the menu opens — a page's day switch changes its history).</summary>
    public void Refresh()
    {
        var page = Page;
        var undoLabel = Available ? _undo.UndoLabel(page) : null;
        var redoLabel = Available ? _undo.RedoLabel(page) : null;
        UndoHeader = undoLabel is null
            ? _localization.Get("Menu.Edit.Undo")
            : string.Format(_localization.Get("Menu.Edit.UndoWithLabel"), undoLabel);
        RedoHeader = redoLabel is null
            ? _localization.Get("Menu.Edit.Redo")
            : string.Format(_localization.Get("Menu.Edit.RedoWithLabel"), redoLabel);
        CanUndo = undoLabel is not null;
        CanRedo = redoLabel is not null;
    }

    // The menu ends any live cell edit first, a text one included (the keys never reach a focused text box).
    [RelayCommand(CanExecute = nameof(CanUndo))]
    private Task Undo() => Available ? _undo.UndoAsync(Page!, includeTextEdit: true) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private Task Redo() => Available ? _undo.RedoAsync(Page!, includeTextEdit: true) : Task.CompletedTask;

    /// <summary>
    /// Ctrl+Z / Ctrl+Y / Ctrl+Shift+Z from the window (focus outside a text box). False when undo does not apply here,
    /// so the key goes on as before.
    /// </summary>
    public bool HandleKey(bool redo)
    {
        if (!Available)
            return false;
        var page = Page!;
        _ = redo ? _undo.RedoAsync(page) : _undo.UndoAsync(page);
        return true;
    }

    // ── The cell menus of the tables

    public string? UndoMenuHeader(SheetTable table) => ForTable(table) && _undo.UndoLabel(Page) is { } label
        ? string.Format(_localization.Get("Menu.Edit.UndoWithLabel"), label)
        : null;

    public string? RedoMenuHeader(SheetTable table) => ForTable(table) && _undo.RedoLabel(Page) is { } label
        ? string.Format(_localization.Get("Menu.Edit.RedoWithLabel"), label)
        : null;

    void ISheetUndoMenu.UndoFromMenu(SheetTable table)
    {
        if (ForTable(table))
            _ = _undo.UndoAsync(Page!, includeTextEdit: true);
    }

    void ISheetUndoMenu.RedoFromMenu(SheetTable table)
    {
        if (ForTable(table))
            _ = _undo.RedoAsync(Page!, includeTextEdit: true);
    }

    // A table inside a dialog (no history of its own) gets no items: a dialog is open then.
    private bool ForTable(SheetTable table) => Available;
}
