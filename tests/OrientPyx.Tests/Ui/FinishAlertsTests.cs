using System.Buffers.Binary;
using System.Reflection;
using OrientPyx.Presentation.Services;
using OrientPyx.Presentation.ViewModels;
using OrientPyx.Presentation.ViewModels.Dialogs;
using OrientPyx.Presentation.ViewModels.Pages;
using OrientPyx.Tests.Support;

namespace OrientPyx.Tests.Ui;

/// <summary>
/// Finish alerts: the two generated sounds are valid WAV files that a judge tells apart by rhythm (one chime vs three
/// beeps), and «Сповіщення…» on the finish page opens «Налаштування сповіщень», whose switches are saved at once to
/// this computer's <c>preferences.json</c>.
/// </summary>
[Collection(AppCollection.Name)]
public sealed class FinishAlertsTests
{
    private readonly HeadlessApp _app;

    public FinishAlertsTests(HeadlessApp app) => _app = app;

    [Theory]
    [InlineData(FinishAlert.CollectChip, 1)]
    [InlineData(FinishAlert.UnknownChip, 3)]
    public void EachSoundIsAWavWithItsOwnRhythm(FinishAlert sound, int bursts)
    {
        var wav = AlertSoundSynth.Wav(sound);

        Assert.Equal("RIFF", Text(wav, 0));
        Assert.Equal("WAVE", Text(wav, 8));
        Assert.Equal("fmt ", Text(wav, 12));
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(20)));   // PCM
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(22)));   // mono
        Assert.Equal(AlertSoundSynth.SampleRate, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24)));
        Assert.Equal(16, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(34)));
        Assert.Equal("data", Text(wav, 36));
        Assert.Equal(wav.Length - 44, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40)));
        Assert.Equal(wav.Length - 8, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(4)));

        var samples = Samples(wav);
        var peak = samples.Max(s => Math.Abs((int)s));
        Assert.InRange(peak, (int)(short.MaxValue * 0.75), (int)(short.MaxValue * 0.81));
        // Starts and ends at silence, so there is no click.
        Assert.Equal(0, samples[0]);
        Assert.InRange(Math.Abs((int)samples[^1]), 0, short.MaxValue / 100);

        Assert.Equal(bursts, Bursts(samples));
        Assert.InRange(AlertSoundSynth.Duration(wav).TotalSeconds, 0.3, 1.0);
    }

    [Fact]
    public void TheWindowsNotificationIsSilentAndEscaped()
    {
        const string message = "A & B <c> \"d\" 'e'";
        var xml = System.Xml.Linq.XDocument.Parse(WindowsToasts.BuildXml("Невідомий чіп 8001099", message));

        var texts = xml.Descendants("text").Select(t => t.Value).ToList();
        Assert.Equal(["Невідомий чіп 8001099", message], texts);
        // Windows plays no sound of its own — the finish alerts play theirs.
        Assert.Equal("true", xml.Root!.Element("audio")?.Attribute("silent")?.Value);
        Assert.Equal("ToastGeneric", xml.Descendants("binding").Single().Attribute("template")?.Value);
        // An ordinary toast waits in Windows' queue; only an important one (the unknown chip) skips it.
        Assert.Null(xml.Root.Attribute("scenario"));

        var important = System.Xml.Linq.XDocument.Parse(WindowsToasts.BuildXml("Невідомий чіп 8001099", message, important: true));
        Assert.Equal("urgent", important.Root!.Attribute("scenario")?.Value);
        Assert.Equal("true", important.Root.Element("audio")?.Attribute("silent")?.Value);
    }

    [Fact]
    public void TheTwoSoundsDiffer()
        => Assert.NotEqual(AlertSoundSynth.Wav(FinishAlert.CollectChip), AlertSoundSynth.Wav(FinishAlert.UnknownChip));

    [Fact]
    public async Task TheFinishPageOpensTheSettingsAndTheyAreSavedAtOnce()
    {
        await OpenCompetitionAsync(TestEnvironment.Instance.SyntheticId);
        await _app.OnUiAsync(() => _app.Vm.OpenFinishReadCommand.ExecuteAsync(null));
        await _app.SettleAsync();
        var page = Assert.IsType<FinishReadViewModel>(Shell().SelectedPage);
        await _app.ScreenshotAsync("finish-alerts-button");

        var shown =_app.OnUiAsync(() => page.OpenAlertSettingsCommand.ExecuteAsync(null));
        await _app.WaitUntilAsync(() => _app.Vm.Dialogs.Current is FinishAlertsViewModel, what: "alert settings open");
        var dialog = (FinishAlertsViewModel)_app.Vm.Dialogs.Current!;
        await _app.SettleAsync();
        await _app.ScreenshotAsync("finish-alerts");

        var before = new UiPreferencesService();
        try
        {
            await _app.OnUiAsync(() =>
            {
                dialog.CollectChipToast = !before.CollectChipToast;
                dialog.UnknownChipSound = !before.UnknownChipSound;
                dialog.UnknownChipSystemNotification = !before.UnknownChipSystemNotification;
                dialog.FlashWhenInactive = !before.FlashWhenInactive;
            });

            // A fresh read of preferences.json sees the switches — nothing waits for «Закрити».
            var saved = new UiPreferencesService();
            Assert.Equal(!before.CollectChipToast, saved.CollectChipToast);
            Assert.Equal(before.CollectChipSound, saved.CollectChipSound);
            Assert.Equal(!before.UnknownChipSound, saved.UnknownChipSound);
            Assert.Equal(before.CollectChipSystemNotification, saved.CollectChipSystemNotification);
            Assert.Equal(!before.UnknownChipSystemNotification, saved.UnknownChipSystemNotification);
            Assert.Equal(!before.FlashWhenInactive, saved.FlashWhenInactive);

            // «▶» and the Windows-notification examples never throw (the test host is muted: no sound, no notification).
            await _app.OnUiAsync(() =>
            {
                dialog.PreviewCollectChipCommand.Execute(null);
                dialog.PreviewUnknownChipCommand.Execute(null);
                dialog.ShowCollectChipNotificationCommand.Execute(null);
                dialog.ShowUnknownChipNotificationCommand.Execute(null);
                dialog.ShowFlashCommand.Execute(null);
            });
            // Muted, the flash example leaves the window as it is (it minimizes the window only for real).
            await _app.SettleAsync();
            Assert.NotEqual(Avalonia.Controls.WindowState.Minimized, await _app.OnUiAsync(() => _app.Window.WindowState));

            // The eye of «У програмі» pops the very toast a read-out would show, over the open dialog.
            var toastsBefore = _app.Vm.Toasts.Items.Count;
            await _app.OnUiAsync(() => dialog.ShowCollectChipToastCommand.Execute(null));
            await _app.WaitUntilAsync(() => _app.Vm.Toasts.Items.Count > toastsBefore, what: "example toast shown");
            var toast = _app.Vm.Toasts.Items[^1];
            Assert.Contains("8001023", toast.Message);
            await _app.SettleAsync();
            await _app.ScreenshotAsync("finish-alerts-example-toast");
            await _app.OnUiAsync(() => toast.DismissCommand.Execute(null));
        }
        finally
        {
            await _app.OnUiAsync(() =>
            {
                dialog.CollectChipToast = before.CollectChipToast;
                dialog.UnknownChipSound = before.UnknownChipSound;
                dialog.UnknownChipSystemNotification = before.UnknownChipSystemNotification;
                dialog.FlashWhenInactive = before.FlashWhenInactive;
                dialog.CloseCommand.Execute(null);
            });
        }

        await shown;
        await _app.WaitUntilAsync(() => _app.Vm.Dialogs.Current is null, what: "alert settings closed");
        Assert.Empty(_app.UiErrors);
    }

    private static string Text(byte[] wav, int offset) => System.Text.Encoding.ASCII.GetString(wav, offset, 4);

    private static short[] Samples(byte[] wav)
    {
        var samples = new short[(wav.Length - 44) / 2];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(44 + i * 2));
        return samples;
    }

    // Runs of sound between silences of at least 20 ms (a 10 ms window is silent below 2 % of full scale).
    private static int Bursts(short[] samples)
    {
        var window = AlertSoundSynth.SampleRate / 100;
        var loud = new List<bool>();
        for (var start = 0; start < samples.Length; start += window)
        {
            var end = Math.Min(samples.Length, start + window);
            var max = 0;
            for (var i = start; i < end; i++)
                max = Math.Max(max, Math.Abs((int)samples[i]));
            loud.Add(max > short.MaxValue / 50);
        }

        var bursts = 0;
        var quiet = 2;
        foreach (var isLoud in loud)
        {
            if (isLoud)
            {
                if (quiet >= 2)
                    bursts++;
                quiet = 0;
            }
            else
            {
                quiet++;
            }
        }

        return bursts;
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
}
