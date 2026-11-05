using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.BusinessLogic.Models.Network;
using OrientPyx.DataAccess.Network.Station;
using OrientPyx.Tests.Support;

namespace OrientPyx.Tests.Core.Network;

/// <summary>
/// A station container talking to the test environment's host over the loopback: the catalog lists the host's
/// competitions by identifier, reads equal the host's, a write made on the station lands on the host and comes back
/// through the change stream, results computed on the station equal the host's, the path mapper rejects anything
/// that is not a plain identifier, repeated idempotency keys do not repeat a write, a job method reports progress,
/// the host-file methods are no-ops, and a revoked token ends the connection.
/// </summary>
[Collection(AppCollection.Name)]
public sealed class RemoteRoundTripTests : IClassFixture<LoopbackHostFixture>
{
    private static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(15);

    private readonly LoopbackHostFixture _f;

    public RemoteRoundTripTests(LoopbackHostFixture fixture) => _f = fixture;

    private IEventStore HostStore => _f.HostServices.GetRequiredService<IEventStore>();
    private IEventStore StationStore => _f.StationServices.GetRequiredService<IEventStore>();
    private IEventCatalogService StationCatalog => _f.StationServices.GetRequiredService<IEventCatalogService>();

    [Fact]
    public async Task CatalogListsHostCompetitionsByIdentifier()
    {
        Assert.Equal(StationState.Connected, _f.Station.State);
        Assert.Equal(WorkspaceRole.Station, _f.StationServices.GetRequiredService<IWorkspaceMode>().Role);
        Assert.Equal(WorkspaceRole.Host, _f.HostServices.GetRequiredService<IWorkspaceMode>().Role);

        var events = await StationCatalog.GetEventsAsync();
        var synthetic = Assert.Single(events, e => e.Identifier == TestEnvironment.Instance.SyntheticId);
        Assert.Equal(synthetic.Identifier, synthetic.FolderPath);
        Assert.All(events, e => Assert.False(Path.IsPathRooted(e.FolderPath), $"host path leaked: {e.FolderPath}"));

        var hostEvent = await TestEnvironment.Instance.GetEventAsync(TestEnvironment.Instance.SyntheticId);
        var hostDays = await HostStore.GetDaysAsync(hostEvent.FolderPath);
        var stationDays = await StationStore.GetDaysAsync(synthetic.FolderPath);
        Assert.Equal(hostDays.Select(d => (d.Id, d.Number, d.Date, d.DefaultDiscipline.Value)),
            stationDays.Select(d => (d.Id, d.Number, d.Date, d.DefaultDiscipline.Value)));
    }

    [Fact]
    public async Task WriteOnStationLandsOnHostAndStreamsBack()
    {
        var c = await CompetitionBuilder.CreateAsync(_f.HostServices, TestIds.Next("net"));
        var hostBus = _f.HostServices.GetRequiredService<IDataChangeBus>();
        var stationBus = _f.StationServices.GetRequiredService<IDataChangeBus>();

        var onHost = new TaskCompletionSource<DataChange>(TaskCreationOptions.RunContinuationsAsynchronously);
        var onStation = new TaskCompletionSource<DataChange>(TaskCreationOptions.RunContinuationsAsynchronously);
        void HostHandler(object? _, DataChange ch) { if (ch.Area == DataArea.Regions && ch.EventFolderPath == c.Folder) onHost.TrySetResult(ch); }
        void StationHandler(object? _, DataChange ch) { if (ch.Area == DataArea.Regions && ch.EventFolderPath == c.Summary.Identifier) onStation.TrySetResult(ch); }
        hostBus.Changed += HostHandler;
        stationBus.Changed += StationHandler;
        try
        {
            await StationStore.AddRegionAsync(c.Summary.Identifier, new Region { Name = "Полтава" });

            var hostRegions = await HostStore.GetRegionsAsync(c.Folder);
            Assert.Contains(hostRegions, r => r.Name == "Полтава");

            var hostChange = await onHost.Task.WaitAsync(EventTimeout);
            Assert.Equal(LoopbackHostFixture.StationName, hostChange.Origin);

            var stationChange = await onStation.Task.WaitAsync(EventTimeout);
            Assert.Equal(LoopbackHostFixture.StationName, stationChange.Origin);
            Assert.Equal(hostChange.Version, stationChange.Version);
        }
        finally
        {
            hostBus.Changed -= HostHandler;
            stationBus.Changed -= StationHandler;
        }

        // The station never publishes on its own: one write gives exactly one Regions event on its bus.
        var stationRegions = await StationStore.GetRegionsAsync(c.Summary.Identifier);
        Assert.Single(stationRegions, r => r.Name == "Полтава");
    }

    [Fact]
    public async Task ResultsComputedOnStationEqualHost()
    {
        var env = TestEnvironment.Instance;
        var hostEvent = await env.GetEventAsync(env.SyntheticId);
        var hostDays = (await HostStore.GetDaysAsync(hostEvent.FolderPath)).OrderBy(d => d.Number).ToList();
        var hostSession = _f.HostServices.GetRequiredService<ISessionService>();
        await hostSession.SelectAsync(hostEvent, hostDays[0]);
        var expected = await ResultsSnapshot.BuildAsync(_f.HostServices.GetRequiredService<ICompetitionEditorService>(), hostSession, hostDays);

        var stationEvent = await StationCatalog.FindByIdentifierAsync(env.SyntheticId);
        Assert.NotNull(stationEvent);
        var stationDays = (await StationStore.GetDaysAsync(stationEvent.FolderPath)).OrderBy(d => d.Number).ToList();
        var stationSession = _f.StationServices.GetRequiredService<ISessionService>();
        await stationSession.SelectAsync(stationEvent, stationDays[0]);
        var actual = await ResultsSnapshot.BuildAsync(_f.StationServices.GetRequiredService<ICompetitionEditorService>(), stationSession, stationDays);

        Assert.Equal(expected, actual);
        Assert.Equal(await File.ReadAllTextAsync(env.GoldenPath(env.SyntheticId)), actual);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("")]
    public async Task PathMapperRejectsAnythingButAnIdentifier(string identifier)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => StationStore.GetDaysAsync(identifier));
    }

    [Fact]
    public async Task RepeatedIdempotencyKeyDoesNotRepeatTheWrite()
    {
        var c = await CompetitionBuilder.CreateAsync(_f.HostServices, TestIds.Next("idem"));
        var channel = _f.Station.Channel!;
        var key = Guid.NewGuid().ToString("N");
        var region = new Region { Name = "Ідемпотентність" };
        object?[] args = [c.Summary.Identifier, region];

        await channel.CallAsync("EventStore", nameof(IEventStore.AddRegionAsync), args, RpcCallKind.Write, typeof(void), idempotencyKey: key);
        await channel.CallAsync("EventStore", nameof(IEventStore.AddRegionAsync), args, RpcCallKind.Write, typeof(void), idempotencyKey: key);

        var regions = await HostStore.GetRegionsAsync(c.Folder);
        Assert.Single(regions, r => r.Name == "Ідемпотентність");

        // A new key is a new call — the unique-name rule of the store (not the cache) decides what happens then.
        await channel.CallAsync("EventStore", nameof(IEventStore.AddRegionAsync), [c.Summary.Identifier, new Region { Name = "Друга" }],
            RpcCallKind.Write, typeof(void));
        Assert.Equal(2, (await HostStore.GetRegionsAsync(c.Folder)).Count);
    }

    [Fact]
    public async Task JobMethodReportsProgressAndReturnsResult()
    {
        var c = await CompetitionBuilder.CreateAsync(_f.HostServices, TestIds.Next("job"));
        await c.AddGroupAsync("Ч21", "31 32");

        var data = new UofParticipantData
        {
            Participants =
            [
                new UofParticipant { FullName = "Іваненко Іван", Group = "Ч21", DayNumbers = [1], Chip = "7001" },
                new UofParticipant { FullName = "Петренко Петро", Group = "Ч21", DayNumbers = [1], Chip = "7002" },
            ],
        };
        var stages = new List<ImportProgress>();
        var progress = new SyncProgress(stages);

        var result = await StationStore.ImportParticipantsBatchAsync(
            c.Summary.Identifier, data, clearFirst: false, daysCreated: 0, ParticipantImportScope.AllDays, progress);

        Assert.Equal(2, result.Added);
        Assert.NotEmpty(stages);
        Assert.Contains(stages, s => s.Stage == ImportStage.Done);
        Assert.Equal(2, (await HostStore.GetParticipantsAsync(c.Folder)).Count);
    }

    [Fact]
    public async Task HostFileMethodsAreNoOpsOnStation()
    {
        await StationStore.CheckpointAsync("no-such-competition");
        await StationStore.ReleaseAsync("no-such-competition");
        // The host would have answered 409 for an unknown folder had the call gone over the wire.
    }

    [Fact]
    public async Task RevokedTokenEndsTheConnection()
    {
        Assert.NotEmpty(_f.Host.Stations);
        await _f.Host.DisconnectAllAsync();
        Assert.Empty(_f.Host.Stations);

        await Assert.ThrowsAsync<PairingRefusedException>(() => StationStore.GetDaysAsync(TestEnvironment.Instance.SyntheticId));
        var deadline = DateTime.UtcNow + EventTimeout;
        while (_f.Station.State != StationState.Disconnected && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        Assert.Equal(StationState.Disconnected, _f.Station.State);
        Assert.Equal(WorkspaceRole.Local, _f.StationServices.GetRequiredService<IWorkspaceMode>().Role);

        // Pair again with the PIN so the other tests of this class keep a live connection whatever their order.
        await _f.ConnectAsync();
        Assert.Equal(StationState.Connected, _f.Station.State);
        Assert.NotEmpty(await StationStore.GetDaysAsync(TestEnvironment.Instance.SyntheticId));
    }

    // IProgress<> that records on the reporting thread (Progress<T> would post to a sync context the tests lack).
    private sealed class SyncProgress(List<ImportProgress> sink) : IProgress<ImportProgress>
    {
        public void Report(ImportProgress value)
        {
            lock (sink)
                sink.Add(value);
        }
    }
}
