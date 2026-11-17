using OrientPyx.Presentation.Services;
using OrientPyx.Presentation.ViewModels.Dialogs;
using OrientPyx.Tests.Support;

namespace OrientPyx.Tests.Ui;

/// <summary>
/// The launch after an update shows «OrientPyx оновлено»: the new version in the message, a link to that version's
/// section of «Що нового», closed by its button.
/// </summary>
[Collection(AppCollection.Name)]
public sealed class AppUpdatedDialogTests
{
    private readonly HeadlessApp _app;

    public AppUpdatedDialogTests(HeadlessApp app) => _app = app;

    [Fact]
    public async Task ShowsTheVersionAndLinksToItsChanges()
    {
        await _app.SettleAsync();
        // Completes when the dialog is closed.
        var shown = _app.OnUiAsync(() => _app.Vm.ShowAppUpdatedAsync("9.8.7"));
        await _app.WaitUntilAsync(() => _app.Vm.Dialogs.Current is AppUpdatedViewModel, what: "update dialog");
        var dialog = (AppUpdatedViewModel)_app.Vm.Dialogs.Current!;
        await _app.SettleAsync();

        Assert.Contains("9.8.7", dialog.Message);
        Assert.Equal(DocsLinks.Changelog("9.8.7"), dialog.ChangelogUri);
        await _app.ScreenshotAsync("app-updated");

        await _app.OnUiAsync(() => dialog.CloseCommand.Execute(null));
        await shown;
        await _app.WaitUntilAsync(() => _app.Vm.Dialogs.Current is null, what: "update dialog closed");
        Assert.Empty(_app.UiErrors);
    }
}
