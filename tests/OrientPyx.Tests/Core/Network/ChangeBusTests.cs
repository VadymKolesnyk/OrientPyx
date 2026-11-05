using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Entities;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Services.Network;
using OrientPyx.BusinessLogic.Services.Store;
using OrientPyx.Tests.Support;
using Xunit;

namespace OrientPyx.Tests.Core.Network;

/// <summary>
/// The change bus and the decorators around the stores: a write through the DI <see cref="IEventStore"/> publishes
/// the areas of <see cref="EventStoreMethods"/> stamped with this machine's origin; the bus keeps a replayable buffer;
/// a station publishes nothing of its own; the app-store writes publish <see cref="DataArea.AppData"/>.
/// </summary>
[Collection(AppCollection.Name)]
public sealed class ChangeBusTests
{
    [Fact]
    public void BusVersionsBufferAndReplay()
    {
        var bus = new DataChangeBus();
        Assert.Equal(0, bus.CurrentVersion("e1"));
        var first = bus.Publish("e1", DataArea.Groups, "me");
        var second = bus.Publish("e1", DataArea.Chips, "me");
        bus.Publish("e2", DataArea.Days, "me");
        Assert.Equal(1, first.Version);
        Assert.Equal(2, second.Version);
        Assert.Equal(2, bus.CurrentVersion("e1"));
        Assert.Equal(1, bus.CurrentVersion("e2"));
        Assert.Equal(3, bus.Sequence);

        var since = bus.Since(first.Sequence)!;
        Assert.Equal([second.Sequence, 3], since.Select(c => c.Sequence).ToArray());
        Assert.Empty(bus.Since(3)!);
        Assert.Null(bus.Since(-5));

        bus.Replay(new DataChange("e1", DataArea.Participants, 40, "host"));
        Assert.Equal(40, bus.CurrentVersion("e1"));
        Assert.Equal(4, bus.Sequence);
    }

    [Fact]
    public async Task WriteThroughTheStorePublishesItsAreas()
    {
        var env = TestEnvironment.Instance;
        var sp = env.Services;
        var bus = sp.GetRequiredService<IDataChangeBus>();
        var store = sp.GetRequiredService<IEventStore>();
        var mode = sp.GetRequiredService<IWorkspaceMode>();
        var c = await CompetitionBuilder.CreateAsync(sp, TestIds.Next("bus"));

        var received = new List<DataChange>();
        void Handler(object? _, DataChange ch)
        {
            if (string.Equals(ch.EventFolderPath, c.Folder, StringComparison.OrdinalIgnoreCase))
                lock (received) received.Add(ch);
        }
        bus.Changed += Handler;
        try
        {
            var before = bus.CurrentVersion(c.Folder);
            await store.AddRegionAsync(c.Folder, new Region { Name = "Bus region" });
            Assert.Single(received);
            Assert.Equal(DataArea.Regions, received[0].Area);
            Assert.Equal(mode.SelfOrigin, received[0].Origin);
            Assert.Equal(before + 1, received[0].Version);

            received.Clear();
            await store.GetRegionsAsync(c.Folder);
            Assert.Empty(received);

            // A multi-area write publishes one event per area (a missing row is a no-op in the store but still a write).
            await store.DeleteParticipantAsync(c.Folder, Guid.NewGuid());
            Assert.Equal(EventStoreMethods.AreasOf(nameof(IEventStore.DeleteParticipantAsync)).ToArray(),
                received.Select(r => r.Area).ToArray());
        }
        finally
        {
            bus.Changed -= Handler;
        }
    }

    [Fact]
    public async Task AppStoreWritesPublishAppData()
    {
        var sp = TestEnvironment.Instance.Services;
        var bus = sp.GetRequiredService<IDataChangeBus>();
        var appStore = sp.GetRequiredService<IAppStore>();

        DataChange? got = null;
        void Handler(object? _, DataChange ch) { if (ch.Area == DataArea.AppData) got = ch; }
        bus.Changed += Handler;
        try
        {
            var rank = await appStore.AddRankAsync();
            Assert.NotNull(got);
            Assert.Equal(DataChangeBus.AnyEvent, got!.EventFolderPath);
            await appStore.DeleteRankAsync(rank.Id);
        }
        finally
        {
            bus.Changed -= Handler;
        }
    }

    [Fact]
    public async Task StationRolePublishesNothing()
    {
        var bus = new DataChangeBus();
        var mode = new WorkspaceMode();
        var inner = System.Reflection.DispatchProxy.Create<IEventStore, CountingProxy>();
        var counter = (CountingProxy)(object)inner;
        var store = NotifyingEventStore.Create(inner, bus, mode);

        mode.SetStation(new BusinessLogic.Models.Network.StationInfo("h", "Host", "127.0.0.1:1", "Me"));
        await store.SetHiddenAsync("e", true);
        Assert.Equal(0, bus.Sequence);
        Assert.Equal(1, counter.Calls);

        mode.SetLocal();
        await store.SetHiddenAsync("e", true);
        Assert.Equal(1, bus.Sequence);
        Assert.Equal(2, counter.Calls);
    }

    /// <summary>A stand-in store that counts calls and completes every method at once.</summary>
    public class CountingProxy : System.Reflection.DispatchProxy
    {
        public int Calls { get; private set; }

        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }
}
