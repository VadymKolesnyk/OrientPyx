using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.Presentation.ViewModels;
using OrientPyx.Presentation.ViewModels.Pages;
using OrientPyx.Tests.Support;
using Xunit.Abstractions;

namespace OrientPyx.Tests.Ui;

/// <summary>
/// The participants page reloads itself on every change another machine makes (a host reloads it for each write of
/// its stations). Those live reloads must not keep memory: on the host of a real competition the process grew to
/// several GB over an hour of «Мандатка» marks from a station.
/// </summary>
[Collection(AppCollection.Name)]
public sealed class ParticipantsReloadMemoryTests
{
    private const int Runners = 400;
    private const int Reloads = 30;

    private static readonly MethodInfo ReloadMethod =
        typeof(PageViewModelBase).GetMethod("ReloadAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private readonly HeadlessApp _app;
    private readonly ITestOutputHelper _output;

    public ParticipantsReloadMemoryTests(HeadlessApp app, ITestOutputHelper output)
    {
        _app = app;
        _output = output;
    }

    private static IServiceProvider Core => TestEnvironment.Instance.Services;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LiveReloads_DoNotKeepMemory(bool roster)
    {
        var c = await CompetitionBuilder.CreateAsync(Core, TestIds.Next(roster ? "ui_memr" : "ui_memd"), dayCount: 2);
        for (var g = 0; g < 20; g++)
            await c.AddGroupAsync($"Ч{10 + g * 2}", "31-32-33");
        for (var i = 1; i <= Runners; i++)
            await c.AddRunnerAsync($"Бігун {i:000}", i.ToString(), (100000 + i).ToString(), $"Ч{10 + (i % 20) * 2}");
        await OpenCompetitionAsync(c.Summary.Identifier);

        await _app.OnUiAsync(() => _app.Vm.OpenParticipantsCommand.ExecuteAsync(null));
        await _app.SettleAsync();
        var page = Assert.IsType<ParticipantsViewModel>(Shell().SelectedPage);
        var wanted = roster
            ? await _app.OnUiAsync(() => page.DayOptions.First(d => d.IsRoster))
            : await _app.OnUiAsync(() => page.DayOptions.First(d => !d.IsRoster));
        if (await _app.OnUiAsync(() => page.SelectedDay != wanted))
        {
            await _app.OnUiAsync(() => page.SelectedDay = wanted);
            await _app.SettleAsync();
        }
        await _app.WaitUntilAsync(() => roster ? page.Roster.Count == Runners : page.Participants.Count == Runners,
            TimeSpan.FromSeconds(60), "rows loaded");

        for (var i = 0; i < 3; i++)
            await ReloadAsync(page);
        var before = Managed();

        for (var i = 1; i <= Reloads; i++)
        {
            await ReloadAsync(page);
            if (i % 10 == 0)
                _output.WriteLine($"{(roster ? "roster" : "day grid")}: after {i} reloads {(Managed() - before) / 1024.0 / 1024.0:+0.0;-0.0} MB");
        }

        var perReload = (Managed() - before) / (double)Reloads;
        _output.WriteLine($"per reload: {perReload / 1024.0:0} KB");
        Assert.True(perReload < 200 * 1024, $"a live reload keeps {perReload / 1024.0:0} KB");
    }

    private async Task ReloadAsync(ParticipantsViewModel page)
    {
        await _app.OnUiAsync(() => (Task)ReloadMethod.Invoke(page, [CancellationToken.None])!);
        await _app.SettleAsync();
    }

    private static long Managed()
    {
        for (var i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        return GC.GetTotalMemory(forceFullCollection: true);
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
