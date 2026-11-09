using System.Globalization;
using System.Reflection;
using CommunityToolkit.Mvvm.Input;
using OrientPyx.Presentation.ViewModels;
using OrientPyx.Presentation.ViewModels.Pages;
using OrientPyx.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace OrientPyx.Tests.Ui;

/// <summary>
/// The real application on the headless platform: the competition picker appears, a competition opens into
/// the shell, every page the menus and the sidebar lead to opens and renders, the plugins page switches a
/// plugin on and off live, the settings overlay opens and the language switches at runtime. Each step saves
/// a screenshot into <see cref="TestEnvironment.ResultsDir"/>; any exception the UI thread reports fails the test.
/// </summary>
[Collection(AppCollection.Name)]
public sealed class HeadlessUiTests
{
    private readonly HeadlessApp _app;
    private readonly ITestOutputHelper _output;

    public HeadlessUiTests(HeadlessApp app, ITestOutputHelper output)
    {
        _app = app;
        _output = output;
    }

    [Fact]
    public async Task A_PickerListsPreparedCompetitionsAndOpensOne()
    {
        var env = TestEnvironment.Instance;
        await _app.SettleAsync();
        // Tests run in no fixed order: another one may have opened a competition already — go back to the picker.
        if (_app.Vm.IsEventSelected)
        {
            await _app.OnUiAsync(() => _app.Vm.ChangeEventCommand.ExecuteAsync(null));
            await _app.SettleAsync();
        }
        var selection = await _app.OnUiAsync(() => Assert.IsType<EventSelectionViewModel>(_app.Vm.CurrentView));
        await _app.WaitUntilAsync(() => selection.Events.Count >= env.EventIds.Count, what: "picker lists the competitions");
        _output.WriteLine($"picker: {await _app.ScreenshotAsync("01-picker")}");

        await _app.OnUiAsync(async () =>
        {
            selection.SelectedEvent = selection.Events.First(e => e.Identifier == env.SyntheticId);
            await selection.OpenCommand.ExecuteAsync(null);
        });
        await _app.WaitUntilAsync(() => _app.Vm.IsEventSelected && _app.Vm.CurrentView is ShellViewModel, what: "shell shown");
        await _app.SettleAsync();
        _output.WriteLine($"shell: {await _app.ScreenshotAsync("02-shell")}");
        AssertNoUiErrors();
    }

    [Fact]
    public async Task B_EveryPageOpensAndRenders()
    {
        await EnsureCompetitionOpenAsync();
        var shell = GetShell();

        // Every «Open…» command of the root view model is a page (settings/about are overlays, tested apart).
        var commands = typeof(MainWindowViewModel).GetProperties()
            .Where(p => p.Name.StartsWith("Open", StringComparison.Ordinal) && p.Name.EndsWith("Command", StringComparison.Ordinal))
            .Where(p => p.Name is not ("OpenSettingsCommand" or "OpenAboutCommand" or "OpenNetworkCommand"))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToList();
        Assert.True(commands.Count >= 20, $"expected the page commands, found {commands.Count}");

        var visited = new List<string>();
        foreach (var property in commands)
        {
            var command = property.GetValue(_app.Vm);
            await _app.OnUiAsync(async () =>
            {
                switch (command)
                {
                    case IAsyncRelayCommand async:
                        Assert.True(async.CanExecute(null), $"{property.Name} cannot execute");
                        await async.ExecuteAsync(null);
                        break;
                    case IRelayCommand sync:
                        Assert.True(sync.CanExecute(null), $"{property.Name} cannot execute");
                        sync.Execute(null);
                        break;
                    default:
                        throw new InvalidOperationException($"{property.Name} is not a command");
                }
            });
            await _app.SettleAsync();

            var page = await _app.OnUiAsync(() => shell.SelectedPage);
            Assert.NotNull(page);
            var name = property.Name["Open".Length..^"Command".Length];
            visited.Add($"{name} → {page.GetType().Name} «{page.Title}»");
            await _app.ScreenshotAsync($"03-page-{name}");
            AssertNoUiErrors(property.Name);
        }

        foreach (var line in visited)
            _output.WriteLine(line);
    }

    [Fact]
    public async Task C_ParticipantsPageShowsTheDayRows()
    {
        await EnsureCompetitionOpenAsync();
        await _app.OnUiAsync(() => _app.Vm.OpenParticipantsCommand.ExecuteAsync(null));
        await _app.SettleAsync();
        var page = Assert.IsType<ParticipantsViewModel>(await _app.OnUiAsync(() => GetShell().SelectedPage));
        await _app.WaitUntilAsync(() => page.Participants.Count + page.Roster.Count > 0, what: "participant rows loaded");
        _output.WriteLine($"participants: day rows {page.Participants.Count}, roster rows {page.Roster.Count}");
        await _app.ScreenshotAsync("04-participants");
        AssertNoUiErrors();
    }

    [Fact]
    public async Task D_PluginsPageSwitchesAPluginOnAndOff()
    {
        await EnsureCompetitionOpenAsync();
        await _app.OnUiAsync(() => _app.Vm.OpenPluginsCommand.ExecuteAsync(null));
        await _app.SettleAsync();
        var page = Assert.IsType<PluginsViewModel>(await _app.OnUiAsync(() => GetShell().SelectedPage));
        await _app.WaitUntilAsync(() => page.Plugins.Count == 2, what: "two standard plugins listed");
        Assert.All(page.Plugins, p => Assert.False(p.IsError, p.Error));
        await _app.ScreenshotAsync("05-plugins-off");

        var weighted = page.Plugins.Single(p => p.Id == "weighted");
        var menuBefore = _app.Vm.PluginMenu.Count;
        await _app.OnUiAsync(() => weighted.Enabled = true);
        await _app.SettleAsync();
        await _app.WaitUntilAsync(() => _app.Vm.PluginMenu.Count == menuBefore + 1, what: "plugin menu gains the plugin's page");
        await _app.ScreenshotAsync("06-plugins-on");

        await _app.OnUiAsync(() => weighted.Enabled = false);
        await _app.SettleAsync();
        await _app.WaitUntilAsync(() => _app.Vm.PluginMenu.Count == menuBefore, what: "plugin menu drops the plugin's page");
        AssertNoUiErrors();
    }

    [Fact]
    public async Task E_SettingsOverlayOpensAndLanguageSwitchesLive()
    {
        await EnsureCompetitionOpenAsync();
        await _app.OnUiAsync(() => _app.Vm.OpenSettingsCommand.Execute(null));
        await _app.SettleAsync();
        Assert.True(_app.Vm.IsSettingsOpen);
        Assert.False(_app.Vm.IsEventSelected, "the settings overlay hides the shell");
        await _app.ScreenshotAsync("07-settings");

        var localization = _app.Vm.Localization;
        var english = localization.AvailableCultures.Single(c => c.Name == "en-US");
        var ukrainian = localization.AvailableCultures.Single(c => c.Name == "uk-UA");
        try
        {
            await _app.OnUiAsync(() => localization.SetLanguage(english));
            await _app.SettleAsync();
            Assert.Equal("Participants", localization.Get("Nav.Participants"));
            Assert.Equal("en-US", CultureInfo.CurrentUICulture.Name);
            await _app.ScreenshotAsync("08-settings-en");
        }
        finally
        {
            await _app.OnUiAsync(() => localization.SetLanguage(ukrainian));
            await _app.SettleAsync();
        }
        Assert.Equal("Учасники", localization.Get("Nav.Participants"));

        await _app.OnUiAsync(() => _app.Vm.CloseSettingsCommand.Execute(null));
        await _app.SettleAsync();
        Assert.True(_app.Vm.IsEventSelected);
        AssertNoUiErrors();
    }

    [Fact]
    public async Task F_AboutOverlayOpens()
    {
        await EnsureCompetitionOpenAsync();
        await _app.OnUiAsync(() => _app.Vm.OpenAboutCommand.Execute(null));
        await _app.SettleAsync();
        Assert.True(_app.Vm.IsAboutOpen);
        await _app.ScreenshotAsync("09-about");
        await _app.OnUiAsync(() => _app.Vm.CloseAboutCommand.Execute(null));
        await _app.SettleAsync();
        AssertNoUiErrors();
    }

    // The synthetic competition specifically: other tests in the collection leave the competitions they create or
    // import open (both open by themselves), and those have no participants.
    private async Task EnsureCompetitionOpenAsync()
    {
        var env = TestEnvironment.Instance;
        if (_app.Vm.IsEventSelected && CurrentIdentifier() == env.SyntheticId)
            return;
        if (_app.Vm.IsEventSelected)
        {
            await _app.OnUiAsync(() => _app.Vm.ChangeEventCommand.ExecuteAsync(null));
            await _app.WaitUntilAsync(() => _app.Vm.CurrentView is EventSelectionViewModel, what: "picker shown");
        }
        await _app.SettleAsync();
        var selection = await _app.OnUiAsync(() => Assert.IsType<EventSelectionViewModel>(_app.Vm.CurrentView));
        await _app.WaitUntilAsync(() => selection.Events.Any(e => e.Identifier == env.SyntheticId), what: "synthetic competition listed");
        await _app.OnUiAsync(async () =>
        {
            selection.SelectedEvent = selection.Events.First(e => e.Identifier == env.SyntheticId);
            await selection.OpenCommand.ExecuteAsync(null);
        });
        await _app.WaitUntilAsync(() => _app.Vm.IsEventSelected, what: "competition opened");
        await _app.SettleAsync();
    }

    private ShellViewModel GetShell()
    {
        var field = typeof(MainWindowViewModel).GetField("_shell", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (ShellViewModel)field.GetValue(_app.Vm)!;
    }

    private string? CurrentIdentifier()
    {
        var field = typeof(MainWindowViewModel).GetField("_session", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return ((OrientPyx.BusinessLogic.Interfaces.ISessionService)field.GetValue(_app.Vm)!).CurrentEvent?.Identifier;
    }

    private void AssertNoUiErrors(string? step = null)
    {
        if (_app.UiErrors.Count == 0)
            return;
        var text = string.Join(Environment.NewLine, _app.UiErrors.Select(e => e.ToString()));
        _app.UiErrors.Clear();
        Assert.Fail($"The UI thread reported {(step is null ? "" : $"during {step} ")}an exception:{Environment.NewLine}{text}");
    }
}
