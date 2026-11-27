using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.DependencyInjection;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.BusinessLogic.Models.Network;
using OrientPyx.BusinessLogic.Serialization;
using OrientPyx.BusinessLogic.Services.Undo;
using OrientPyx.DataAccess.DependencyInjection;
using OrientPyx.DataAccess.Network.Host;
using OrientPyx.DataAccess.Network.Protocol;
using OrientPyx.DataAccess.Network.Station;
using OrientPyx.DataAccess.Persistence.Undo;
using OrientPyx.Localization.DependencyInjection;
using OrientPyx.Tests.Support;

namespace OrientPyx.Tests.Core.Network;

/// <summary>
/// Undo over the network (§8 of docs/undo-redo-plan.md): a station's write carries its undo step to the host, which
/// journals it under the station's token; the station reverts and redoes it; a station of the same name (another
/// device) cannot see it, nor can the host itself; a host that comes back with a new change-stream epoch raises
/// <see cref="INetworkClient.HostRestarted"/> on the station.
/// </summary>
[Collection(AppCollection.Name)]
public sealed class UndoNetworkTests : IClassFixture<LoopbackHostFixture>
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly LoopbackHostFixture _f;

    public UndoNetworkTests(LoopbackHostFixture fixture) => _f = fixture;

    private IEventStore HostStore => _f.HostServices.GetRequiredService<IEventStore>();

    // A competition on the host with one runner, opened on the station.
    private async Task<CompetitionBuilder> BuildAndOpenOnStationAsync()
    {
        var c = await CompetitionBuilder.CreateAsync(_f.HostServices, TestIds.Next("undo_net"));
        await c.AddGroupAsync("Ч21", "31");
        await c.AddRunnerAsync("Іваненко Іван", "1", "1001", "Ч21");

        var catalog = _f.StationServices.GetRequiredService<IEventCatalogService>();
        var ev = await catalog.FindByIdentifierAsync(c.Summary.Identifier);
        Assert.NotNull(ev);
        var days = await _f.StationServices.GetRequiredService<IEventStore>().GetDaysAsync(ev.FolderPath);
        await _f.StationServices.GetRequiredService<ISessionService>().SelectAsync(ev, days.Single());
        return c;
    }

    private async Task<Guid> StationStepAsync(Func<ICompetitionEditorService, Task> action)
    {
        var step = Guid.NewGuid();
        using (UndoScope.Enter(step))
            await action(_f.StationServices.GetRequiredService<ICompetitionEditorService>());
        return step;
    }

    private async Task<string> HostChipAsync(CompetitionBuilder c) => (await c.GetLinkAsync("Іваненко Іван", 1)).Chip;

    [Fact]
    public async Task AStationsStepIsJournalledOnTheHostUnderItsTokenAndRevertedFromTheStation()
    {
        var c = await BuildAndOpenOnStationAsync();
        var runner = c.Runners["Іваненко Іван"].Id;

        var step = await StationStepAsync(editor => editor.SetParticipantDayChipAsync(runner, c.Day(1).Id, "2001"));
        Assert.Equal("2001", await HostChipAsync(c));

        // The header reached the host: the step is there, owned by the station's token — not by the host itself.
        Assert.Equal(UndoLookup.Found, UndoJournal.Shared.Find(c.Folder, step, _f.Station.Channel!.Token, out _));
        Assert.Equal(UndoLookup.NotFound, UndoJournal.Shared.Find(c.Folder, step, null, out _));

        var history = _f.StationServices.GetRequiredService<IEditHistoryService>();
        var undo = await history.RevertAsync(step);
        Assert.Equal(UndoRevertStatus.Done, undo.Result.Status);
        Assert.Equal("1001", await HostChipAsync(c));

        var redo = await history.RevertAsync(undo.RevertStepId);
        Assert.Equal(UndoRevertStatus.Done, redo.Result.Status);
        Assert.Equal("2001", await HostChipAsync(c));

        // The host cannot revert the station's step on its own behalf either.
        await _f.HostServices.GetRequiredService<ISessionService>().SelectAsync(c.Summary, c.Day(1));
        var local = await _f.HostServices.GetRequiredService<IEditHistoryService>().RevertAsync(redo.RevertStepId);
        Assert.Equal(UndoRevertStatus.NotFound, local.Result.Status);
    }

    [Fact]
    public async Task AStationsAssistantWriteIsStampedWithTheMcpOriginOnTheHost()
    {
        var c = await BuildAndOpenOnStationAsync();
        var runner = c.Runners["Іваненко Іван"].Id;
        var bus = _f.HostServices.GetRequiredService<IDataChangeBus>();
        var origins = new List<string>();
        void OnChanged(object? sender, DataChange change)
        {
            if (change.EventFolderPath.EndsWith(c.Summary.Identifier, StringComparison.OrdinalIgnoreCase))
                lock (origins)
                    origins.Add(change.Origin);
        }
        bus.Changed += OnChanged;
        try
        {
            // The station's own write carries its name; its assistant's write (McpWriter sets the «·mcp» origin on the
            // flow) is tagged, so the host stamps «<station>·mcp» and the station's pages reload after it too.
            await StationStepAsync(editor => editor.SetParticipantDayChipAsync(runner, c.Day(1).Id, "2001"));
            var mode = _f.StationServices.GetRequiredService<IWorkspaceMode>();
            await StationStepAsync(async editor =>
            {
                BusinessLogic.Services.Network.DataChangeOrigin.Current = BusinessLogic.Services.Mcp.McpOrigin.For(mode);
                try
                {
                    await editor.SetParticipantDayChipAsync(runner, c.Day(1).Id, "2002");
                }
                finally
                {
                    BusinessLogic.Services.Network.DataChangeOrigin.Current = null;
                }
            });
            Assert.Equal("2002", await HostChipAsync(c));
            lock (origins)
            {
                Assert.Contains(LoopbackHostFixture.StationName, origins);
                Assert.Contains(BusinessLogic.Services.Mcp.McpOrigin.ForStation(LoopbackHostFixture.StationName), origins);
            }
        }
        finally
        {
            bus.Changed -= OnChanged;
        }
    }

    [Fact]
    public async Task AnotherDeviceWithTheSameStationNameCannotRevertTheStep()
    {
        var c = await BuildAndOpenOnStationAsync();
        var runner = c.Runners["Іваненко Іван"].Id;
        var step = await StationStepAsync(editor => editor.SetParticipantDayChipAsync(runner, c.Day(1).Id, "2001"));

        // A second machine pairs under the very same name (names are neither unique nor stable; the token is).
        using var other = new RpcChannel(_f.Endpoint);
        var pair = new PairRequest(_f.Host.Pin, LoopbackHostFixture.StationName, Wire.AppVersion, NetworkProtocol.Version, [],
            DeviceId: "another-device-" + Guid.NewGuid().ToString("N"));
        using (var response = await other.Http.PostAsync(Wire.PairPath, JsonContent.Create(pair, options: RpcJson.Options)))
        {
            response.EnsureSuccessStatusCode();
            other.Token = (await response.Content.ReadFromJsonAsync<PairResponse>(RpcJson.Options))!.Token;
        }
        try
        {
            Assert.NotEqual(_f.Station.Channel!.Token, other.Token);
            UndoRevertResult? result;
            using (UndoScope.Enter(Guid.NewGuid()))
                result = (UndoRevertResult?)await other.CallAsync("EventStore", nameof(IEventStore.RevertUndoStepAsync),
                    [c.Summary.Identifier, step], RpcCallKind.Write, typeof(UndoRevertResult));

            Assert.Equal(UndoRevertStatus.NotFound, result!.Status);
            Assert.Equal("2001", await HostChipAsync(c));

            // Its owner still can.
            var undo = await _f.StationServices.GetRequiredService<IEditHistoryService>().RevertAsync(step);
            Assert.Equal(UndoRevertStatus.Done, undo.Result.Status);
            Assert.Equal("1001", await HostChipAsync(c));
        }
        finally
        {
            await _f.Host.DisconnectStationAsync(other.Token);
        }
    }

    [Fact]
    public async Task ANewChangeStreamEpochRaisesHostRestarted()
    {
        // The epoch belongs to the host process (its change stream), so a «restarted host» is a second host container
        // over the same app.db (same host id, same station tokens) taking over the port.
        var port = _f.Host.Info!.Port;
        var restarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnRestarted(object? sender, EventArgs e) => restarted.TrySetResult();
        _f.Station.HostRestarted += OnRestarted;
        var recorderLog = UndoRecorder.Log;
        ServiceProvider? secondServices = null;
        NetworkHost? second = null;
        try
        {
            await _f.Host.StopAsync();

            var services = new ServiceCollection();
            services.AddOrientPyxLocalization();
            services.AddOrientPyxBusinessLogic();
            services.AddOrientPyxDataAccess();
            secondServices = services.BuildServiceProvider();
            secondServices.InitializeOrientPyxDatabase();
            second = (NetworkHost)secondServices.GetRequiredService<INetworkHost>();
            await second.StartAsync(new HostStartOptions(IPAddress.Loopback, port));
            Assert.Equal(port, second.Info!.Port);

            await restarted.Task.WaitAsync(Timeout);
        }
        finally
        {
            _f.Station.HostRestarted -= OnRestarted;
            if (second is not null)
                await second.StopAsync();
            if (secondServices is not null)
                await secondServices.DisposeAsync();
            UndoRecorder.Log = recorderLog;

            // The fixture's host back on the same port, so the other tests of the class keep a live station.
            await _f.Host.StartAsync(new HostStartOptions(IPAddress.Loopback, port));
        }

        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await _f.StationServices.GetRequiredService<IEventStore>().GetDaysAsync(TestEnvironment.Instance.SyntheticId);
                break;
            }
            catch (Exception) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(200);
            }
        }
        Assert.Equal(StationState.Connected, _f.Station.State);
    }
}
