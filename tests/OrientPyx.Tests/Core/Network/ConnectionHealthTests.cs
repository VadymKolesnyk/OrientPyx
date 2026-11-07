using OrientPyx.DataAccess.Network.Station;

namespace OrientPyx.Tests.Core.Network;

/// <summary>
/// The station's offline decision: a failure alone does not make it offline, only a failure plus
/// <see cref="ConnectionHealth.OfflineGrace"/> without any answer from the host; any answer brings it back at once; long
/// silence without a failure is not offline either. Writes waiting longer than <see cref="ConnectionHealth.SlowWrite"/>
/// are counted as delayed.
/// </summary>
public sealed class ConnectionHealthTests
{
    private DateTimeOffset _now = new(2026, 11, 7, 10, 0, 0, TimeSpan.Zero);

    private ConnectionHealth NewHealth() => new(() => _now);

    [Fact]
    public void ShortOutage_StaysOnline()
    {
        using var health = NewHealth();
        health.ReportFailure("change stream: silent");
        _now += ConnectionHealth.OfflineGrace - TimeSpan.FromSeconds(1);
        health.Evaluate();
        Assert.False(health.IsOffline);

        // The host answers on another connection (a version poll) — the outage is over before it counted.
        health.ReportSuccess();
        _now += TimeSpan.FromSeconds(10);
        health.Evaluate();
        Assert.False(health.IsOffline);
    }

    [Fact]
    public void FailureAndNoAnswerForTheGrace_GoesOffline_AndAnyAnswerBringsItBack()
    {
        using var health = NewHealth();
        var changes = 0;
        health.Changed += () => changes++;

        health.ReportFailure("EventStore.GetDaysAsync: timeout");
        _now += ConnectionHealth.OfflineGrace;
        health.Evaluate();
        Assert.True(health.IsOffline);
        Assert.Equal("EventStore.GetDaysAsync: timeout", health.LastFailure);
        Assert.Equal(1, changes);

        health.ReportSuccess(); // re-evaluates at once
        Assert.False(health.IsOffline);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void SilenceWithoutFailure_IsNotOffline()
    {
        using var health = NewHealth();
        _now += TimeSpan.FromMinutes(5);
        health.Evaluate();
        Assert.False(health.IsOffline);
    }

    [Fact]
    public void SlowWrites_AreCountedUntilTheyEnd()
    {
        using var health = NewHealth();
        var quick = health.BeginWrite();
        health.EndWrite(quick);

        var slow = health.BeginWrite();
        health.Evaluate();
        Assert.Equal(0, health.DelayedWrites);

        _now += ConnectionHealth.SlowWrite;
        health.Evaluate();
        Assert.Equal(1, health.DelayedWrites);

        health.EndWrite(slow);
        Assert.Equal(0, health.DelayedWrites);
    }
}
