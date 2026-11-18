using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.Presentation.Controls;
using OrientPyx.Presentation.ViewModels;
using OrientPyx.Presentation.ViewModels.Dialogs;
using OrientPyx.Presentation.ViewModels.Pages;
using OrientPyx.Tests.Support;

namespace OrientPyx.Tests.Ui;

/// <summary>
/// «Зчитка на фініші»: F2 opens the edit window and Ctrl+P prints the split slip of the selected row (the row of
/// the clicked cell) — through real key presses on the headless window, also after a closed modal took the focus.
/// </summary>
[Collection(AppCollection.Name)]
public sealed class FinishReadHotkeysTests
{
    private readonly HeadlessApp _app;

    public FinishReadHotkeysTests(HeadlessApp app) => _app = app;

    private static IServiceProvider Core => TestEnvironment.Instance.Services;

    [Fact]
    public async Task F2EditsAndCtrlPPrintsTheSelectedRow()
    {
        await OpenCompetitionAsync(TestEnvironment.Instance.SyntheticId);
        await _app.OnUiAsync(() => _app.Vm.OpenFinishReadCommand.ExecuteAsync(null));
        await _app.SettleAsync();
        var page = Assert.IsType<FinishReadViewModel>(Shell().SelectedPage);
        await _app.WaitUntilAsync(() => page.Readouts.Count > 1, what: "read-outs listed");
        var table = await _app.OnUiAsync(() =>
            _app.Window.GetVisualDescendants().OfType<SheetTable>().Single(t => t.Name == "Sheet"));
        await _app.WaitUntilAsync(() => table.VisibleItems.Count > 1, what: "the log shows the rows");
        await _app.SettleAsync();

        // Click a cell of the second row: its row becomes the selected one.
        var window = _app.Window;
        var row = await _app.OnUiAsync(() => (FinishReadRowViewModel)table.VisibleItems[1]!);
        var point = await _app.OnUiAsync(() => CellCenter(table, row));
        await _app.OnUiAsync(() => window.MouseDown(point, MouseButton.Left));
        await _app.OnUiAsync(() => window.MouseUp(point, MouseButton.Left));
        await _app.SettleAsync();
        await _app.WaitUntilAsync(() => ReferenceEquals(page.SelectedReadout, row), what: "the clicked row selected");

        // F2 → the edit window of that read-out.
        await _app.OnUiAsync(() => window.KeyPress(Key.F2, RawInputModifiers.None, PhysicalKey.F2, null));
        await _app.WaitUntilAsync(() => _app.Vm.Dialogs.Current is FinishReadoutEditViewModel, what: "edit window open");
        var edit = (FinishReadoutEditViewModel)_app.Vm.Dialogs.Current!;
        Assert.Equal(row.ChipNumber, edit.ChipNumber);

        // F2 again inside the open window does not open a second one.
        await _app.OnUiAsync(() => window.KeyPress(Key.F2, RawInputModifiers.None, PhysicalKey.F2, null));
        await _app.SettleAsync();
        Assert.Same(edit, _app.Vm.Dialogs.Current);
        await _app.OnUiAsync(() => edit.CancelCommand.Execute(null));
        await _app.WaitUntilAsync(() => _app.Vm.Dialogs.Current is null, what: "edit window closed");
        await _app.SettleAsync();

        // Ctrl+P after the modal closed (focus dropped) → the print path of the same row. No printer is set up in
        // the test data root, so it stops at the print settings (or «Windows only» elsewhere) — nothing prints.
        var print = await Core.GetRequiredService<IAppSettingsService>().GetPrintSettingsAsync();
        if (!print.HasPrinter)
        {
            await _app.OnUiAsync(() => window.KeyPress(Key.P, RawInputModifiers.Control, PhysicalKey.P, "p"));
            await _app.WaitUntilAsync(() => _app.Vm.Dialogs.Current is PrintSettingsViewModel or ConfirmDialogViewModel,
                what: "print settings (or the «Windows only» notice) open");
            await _app.OnUiAsync(() =>
            {
                if (_app.Vm.Dialogs.Current is PrintSettingsViewModel settings)
                    settings.CancelCommand.Execute(null);
                else if (_app.Vm.Dialogs.Current is ConfirmDialogViewModel notice)
                    notice.CancelCommand.Execute(null);
            });
            await _app.WaitUntilAsync(() => _app.Vm.Dialogs.Current is null, what: "print dialog closed");
        }
        AssertNoUiErrors();
    }

    // The window-space centre of a realised cell of the given row (the first one found).
    private static Point CellCenter(SheetTable table, object row)
    {
        foreach (var d in table.GetVisualDescendants())
        {
            if (d.GetType().Name != "SheetCell" || d is not Control cell || !ReferenceEquals(cell.DataContext, row)
                || cell.Bounds.Width <= 0)
                continue;
            var centre = new Point(cell.Bounds.Width / 2, cell.Bounds.Height / 2);
            return cell.TranslatePoint(centre, TopLevel.GetTopLevel(table)!) ?? throw new InvalidOperationException("cell not in the tree");
        }
        throw new InvalidOperationException("no realised cell for the row");
    }

    private async Task OpenCompetitionAsync(string identifier)
    {
        await _app.SettleAsync();
        if (_app.Vm.IsEventSelected)
        {
            await _app.OnUiAsync(() => _app.Vm.ChangeEventCommand.ExecuteAsync(null));
            await _app.SettleAsync();
        }
        else if (_app.Vm.CurrentView is CreateEventViewModel create)
        {
            await _app.OnUiAsync(() => create.CancelCommand.Execute(null));
            await _app.SettleAsync();
        }
        await _app.WaitUntilAsync(() => _app.Vm.CurrentView is EventSelectionViewModel, what: "picker shown");
        var selection = (EventSelectionViewModel)_app.Vm.CurrentView;
        if (!selection.Events.Any(e => e.Identifier == identifier))
        {
            var items = await selection.FetchEventsAsync();
            await _app.OnUiAsync(() => selection.Populate(items));
        }
        await _app.WaitUntilAsync(() => selection.Events.Any(e => e.Identifier == identifier), what: $"{identifier} listed");
        await _app.OnUiAsync(async () =>
        {
            selection.SelectedEvent = selection.Events.Single(e => e.Identifier == identifier);
            await selection.OpenCommand.ExecuteAsync(null);
        });
        await _app.WaitUntilAsync(() => _app.Vm.IsEventSelected, what: $"{identifier} opened");
        await _app.SettleAsync();
    }

    private ShellViewModel Shell()
    {
        var field = typeof(MainWindowViewModel).GetField("_shell", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (ShellViewModel)field.GetValue(_app.Vm)!;
    }

    private void AssertNoUiErrors()
    {
        if (_app.UiErrors.Count == 0)
            return;
        var text = string.Join(Environment.NewLine, _app.UiErrors.Select(e => e.ToString()));
        _app.UiErrors.Clear();
        Assert.Fail("The UI thread reported an exception:" + Environment.NewLine + text);
    }
}
