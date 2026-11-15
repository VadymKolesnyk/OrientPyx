using OrientPyx.Presentation.Services;
using OrientPyx.Presentation.ViewModels;
using OrientPyx.Presentation.ViewModels.Dialogs;
using OrientPyx.Tests.Support;

namespace OrientPyx.Tests.Ui;

/// <summary>
/// «Вибір змагання» → кошик: one dialog confirms and offers the archive. «Зберегти архів і видалити» writes the
/// <c>.opyx</c> before deleting, a cancelled save deletes nothing, «Видалити без архіву» deletes straight away.
/// </summary>
[Collection(AppCollection.Name)]
public sealed class DeleteEventUiTests
{
    private readonly HeadlessApp _app;

    public DeleteEventUiTests(HeadlessApp app) => _app = app;

    private static IServiceProvider Core => TestEnvironment.Instance.Services;

    [Fact]
    public async Task DeleteAsksAndOffersTheArchiveFirst()
    {
        var withArchive = await CompetitionBuilder.CreateAsync(Core, TestIds.Next("ui_del_arc"));
        await withArchive.AddGroupAsync("Ч21", "31");
        await withArchive.AddRunnerAsync("Бігун 01", "1", "", "Ч21");
        var without = await CompetitionBuilder.CreateAsync(Core, TestIds.Next("ui_del_raw"));
        var selection = await PickerAsync(withArchive.Summary.Identifier, without.Summary.Identifier);

        var flow = _app.Vm.ArchiveFlow;
        var windowPicker = flow.Picker;
        var archive = Path.Combine(TestEnvironment.ResultsDir, $"{withArchive.Summary.Identifier}.opyx");
        try
        {
            // The save dialog cancelled: nothing is deleted.
            flow.Picker = new FakePicker(null);
            await ChooseAsync(selection, withArchive.Summary.Identifier, d => d.ConfirmCommand.Execute(null));
            await _app.SettleAsync();
            Assert.True(Directory.Exists(withArchive.Folder));
            Assert.Contains(selection.Events, e => e.Identifier == withArchive.Summary.Identifier);

            // «Зберегти архів і видалити»: the archive is written, then the competition goes.
            flow.Picker = new FakePicker(archive);
            await ChooseAsync(selection, withArchive.Summary.Identifier, d => d.ConfirmCommand.Execute(null));
            await _app.WaitUntilAsync(() => !Directory.Exists(withArchive.Folder), what: "competition folder deleted");
            Assert.True(File.Exists(archive));
            await _app.WaitUntilAsync(() => selection.Events.All(e => e.Identifier != withArchive.Summary.Identifier),
                what: "row removed");
        }
        finally
        {
            flow.Picker = windowPicker;
        }

        // «Видалити без архіву».
        await ChooseAsync(selection, without.Summary.Identifier, d => d.AlternateCommand.Execute(null));
        await _app.WaitUntilAsync(() => !Directory.Exists(without.Folder), what: "deleted without an archive");
        await _app.WaitUntilAsync(() => selection.Events.All(e => e.Identifier != without.Summary.Identifier),
            what: "row removed");
        await _app.SettleAsync();
        Assert.Empty(_app.UiErrors);
    }

    // Starts the delete of a row and presses a button of the dialog it opens.
    private async Task ChooseAsync(EventSelectionViewModel selection, string identifier, Action<ConfirmDialogViewModel> press)
    {
        await _app.SettleAsync();
        var row = selection.Events.Single(e => e.Identifier == identifier);
        _ = _app.OnUiAsync(() => selection.DeleteCommand.ExecuteAsync(row));
        await _app.WaitUntilAsync(() => _app.Vm.Dialogs.Current is ConfirmDialogViewModel { TitleKey: "DeleteEvent.Title" },
            what: "delete dialog");
        var dialog = (ConfirmDialogViewModel)_app.Vm.Dialogs.Current!;
        Assert.Equal("DeleteEvent.DeleteWithoutArchive", dialog.AlternateKey);
        await _app.OnUiAsync(() => press(dialog));
    }

    private async Task<EventSelectionViewModel> PickerAsync(params string[] identifiers)
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
        var items = await selection.FetchEventsAsync();
        await _app.OnUiAsync(() => selection.Populate(items));
        await _app.WaitUntilAsync(() => identifiers.All(id => selection.Events.Any(e => e.Identifier == id)),
            what: "competitions listed");
        return selection;
    }

    private sealed class FakePicker(string? savePath) : IArchiveFilePicker
    {
        public Task<string?> PickSaveArchiveAsync(string suggestedFileName) => Task.FromResult(savePath);

        public Task<string?> PickOpenArchiveAsync() => Task.FromResult<string?>(null);
    }
}
