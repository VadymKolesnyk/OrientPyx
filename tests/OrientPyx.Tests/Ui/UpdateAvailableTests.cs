using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.Presentation.Services;
using OrientPyx.Presentation.ViewModels.Dialogs;
using OrientPyx.Tests.Support;

namespace OrientPyx.Tests.Ui;

/// <summary>
/// The quiet update check at start-up: nothing on screen when there is no update (or no connection), a clickable toast
/// when there is one; a real click on the toast opens «Доступна нова версія»; a failed download keeps the dialog open
/// with a note; «Пропустити цю версію» silences that version (and only that one) on later launches.
/// </summary>
[Collection(AppCollection.Name)]
public sealed class UpdateAvailableTests
{
    private readonly HeadlessApp _app;

    public UpdateAvailableTests(HeadlessApp app) => _app = app;

    [Fact]
    public async Task ToastOpensTheDialogAndSkipSilencesThatVersion()
    {
        await _app.SettleAsync();
        await _app.OnUiAsync(() => _app.Vm.Toasts.Items.Clear());
        var updates = new FakeUpdates();
        var preferences = new FakePreferences();
        var check = new StartupUpdateCheck(updates, preferences, _app.Vm.Toasts, _app.Vm.Dialogs, _app.Vm.Localization,
            new FakeLog());

        // Up to date, offline or a failed check: the service answers null — nothing is shown.
        await _app.OnUiAsync(check.RunAsync);
        await _app.SettleAsync();
        Assert.Empty(_app.Vm.Toasts.Items);

        // A newer version: one clickable toast naming it.
        updates.Version = "9.9.9";
        await _app.OnUiAsync(check.RunAsync);
        await _app.WaitUntilAsync(() => _app.Vm.Toasts.Items.Count == 1, what: "update toast");
        var toast = _app.Vm.Toasts.Items[0];
        Assert.True(toast.CanOpen);
        Assert.Contains("9.9.9", toast.Message);
        await _app.SettleAsync();
        await _app.ScreenshotAsync("update-toast");

        // A real click on the toast's body (not its ✕) opens the dialog and closes the toast.
        var point = await _app.OnUiAsync(() =>
        {
            var border = _app.Window.GetVisualDescendants().OfType<Border>()
                .First(b => b.Classes.Contains("toast") && ReferenceEquals(b.DataContext, toast));
            return border.TranslatePoint(new Point(40, 12), _app.Window)
                   ?? throw new InvalidOperationException("toast not in the window");
        });
        await _app.OnUiAsync(() => _app.Window.MouseDown(point, MouseButton.Left));
        await _app.OnUiAsync(() => _app.Window.MouseUp(point, MouseButton.Left));
        await _app.WaitUntilAsync(() => _app.Vm.Dialogs.Current is UpdateAvailableViewModel, what: "update dialog");
        var dialog = (UpdateAvailableViewModel)_app.Vm.Dialogs.Current!;
        Assert.Empty(_app.Vm.Toasts.Items);
        Assert.Contains("9.9.9", dialog.Message);
        Assert.Equal(DocsLinks.Changelog("9.9.9"), dialog.ChangelogUri);
        await _app.SettleAsync();
        await _app.ScreenshotAsync("update-available");

        // «Завантажити й перезапустити» that fails: the dialog stays, says so, and the buttons work again.
        await _app.OnUiAsync(() => dialog.InstallCommand.ExecuteAsync(null));
        await _app.WaitUntilAsync(() => dialog.Failed && dialog.IsIdle, what: "failed download noted");
        Assert.Equal(1, updates.DownloadCalls);
        Assert.Same(dialog, _app.Vm.Dialogs.Current);
        await _app.SettleAsync();
        await _app.ScreenshotAsync("update-available-failed");

        // «Пропустити цю версію»: closed and remembered.
        await _app.OnUiAsync(() => dialog.SkipCommand.Execute(null));
        await _app.WaitUntilAsync(() => _app.Vm.Dialogs.Current is null, what: "update dialog closed");
        Assert.Equal("9.9.9", preferences.SkippedUpdateVersion);

        // The next launch says nothing about the skipped version…
        await _app.OnUiAsync(check.RunAsync);
        await _app.SettleAsync();
        Assert.Empty(_app.Vm.Toasts.Items);

        // …but offers a newer one; «Закрити» changes nothing.
        updates.Version = "10.0.0";
        await _app.OnUiAsync(check.RunAsync);
        await _app.WaitUntilAsync(() => _app.Vm.Toasts.Items.Count == 1, what: "newer update toast");
        await _app.OnUiAsync(() => _app.Vm.Toasts.Items[0].OpenCommand.Execute(null));
        await _app.WaitUntilAsync(() => _app.Vm.Dialogs.Current is UpdateAvailableViewModel, what: "second dialog");
        await _app.OnUiAsync(() => ((UpdateAvailableViewModel)_app.Vm.Dialogs.Current!).CloseCommand.Execute(null));
        await _app.WaitUntilAsync(() => _app.Vm.Dialogs.Current is null, what: "second dialog closed");
        Assert.Equal("9.9.9", preferences.SkippedUpdateVersion);

        Assert.Empty(_app.UiErrors);
    }

    [Fact]
    public async Task DialogOverAnotherDialogGivesItBack()
    {
        await _app.SettleAsync();
        var underneath = _app.OnUiAsync(() => _app.Vm.ShowAppUpdatedAsync("9.8.7"));
        await _app.WaitUntilAsync(() => _app.Vm.Dialogs.Current is AppUpdatedViewModel, what: "first dialog");
        var first = (AppUpdatedViewModel)_app.Vm.Dialogs.Current!;

        var dialog = new UpdateAvailableViewModel(_app.Vm.Localization, new FakeUpdates(), "9.9.9");
        var shown = _app.OnUiAsync(() => _app.Vm.Dialogs.ShowUpdateAvailableAsync(dialog));
        await _app.WaitUntilAsync(() => ReferenceEquals(_app.Vm.Dialogs.Current, dialog), what: "update dialog on top");
        await _app.OnUiAsync(() => dialog.CloseCommand.Execute(null));
        await shown;
        await _app.WaitUntilAsync(() => ReferenceEquals(_app.Vm.Dialogs.Current, first), what: "first dialog back");

        await _app.OnUiAsync(() => first.CloseCommand.Execute(null));
        await underneath;
        await _app.WaitUntilAsync(() => _app.Vm.Dialogs.Current is null, what: "all closed");
        Assert.Empty(_app.UiErrors);
    }

    private sealed class FakeUpdates : IUpdateService
    {
        public string? Version { get; set; }

        public int DownloadCalls { get; private set; }

        public bool IsInstalled => true;

        public Func<Task>? BeforeRestart { get; set; }

        public Task<string?> CheckForUpdateAsync(CancellationToken cancellationToken = default) => Task.FromResult(Version);

        public Task<bool> DownloadAndRestartAsync(IProgress<int>? progress = null, CancellationToken cancellationToken = default)
        {
            DownloadCalls++;
            progress?.Report(50);
            return Task.FromResult(false);
        }
    }

    private sealed class FakePreferences : IUiPreferencesService
    {
        public SplitsDock SplitsDock { get; set; }
        public double SplitsSize { get; set; }
        public double SplitsPrescribedWidth { get; set; }
        public bool CollectChipToast { get; set; }
        public bool CollectChipSound { get; set; }
        public bool UnknownChipSound { get; set; }
        public bool CollectChipSystemNotification { get; set; }
        public bool UnknownChipSystemNotification { get; set; }
        public bool FlashWhenInactive { get; set; }
        public string? SkippedUpdateVersion { get; set; }
    }

    private sealed class FakeLog : IActivityLog
    {
        public void Action(string message) { }
        public void Info(string message) { }
        public void Error(string context, Exception exception) { }
        public void UseEventFolder(string eventFolderPath) { }
        public void UseSharedFolder() { }
        public string LogFilePath => string.Empty;
    }
}
