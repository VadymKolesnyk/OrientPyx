using OrientPyx.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace OrientPyx.Tests.Ui;

/// <summary>
/// The «Спільна робота» (network mode) overlay on the headless app: it opens from the root view model, the host
/// switches on (binding a real port on this machine), the PIN / addresses / self-checks render, the top-bar role
/// indicator appears, the host switches off again and the overlay closes — with no UI-thread exceptions.
/// </summary>
[Collection(AppCollection.Name)]
public sealed class NetworkPageTests
{
    private readonly HeadlessApp _app;
    private readonly ITestOutputHelper _output;

    public NetworkPageTests(HeadlessApp app, ITestOutputHelper output)
    {
        _app = app;
        _output = output;
    }

    [Fact]
    public async Task NetworkOverlayOpensAndTheHostSwitchesOnAndOff()
    {
        await _app.SettleAsync();
        await _app.OnUiAsync(() => _app.Vm.OpenNetworkCommand.Execute(null));
        await _app.SettleAsync();
        Assert.True(_app.Vm.IsNetworkOpen);
        var network = _app.Vm.Network;
        Assert.False(network.IsHostRunning);
        _output.WriteLine($"overlay: {await _app.ScreenshotAsync("10-network")}");

        try
        {
            await _app.OnUiAsync(() => network.IsHostEnabled = true);
            await _app.WaitUntilAsync(() => !network.IsHostBusy, what: "host switch applied");
            Assert.True(network.IsHostRunning, $"the host did not start: {network.HostError}");
            Assert.NotEmpty(network.PinDisplay);
            await _app.WaitUntilAsync(() => !network.IsHostChecking, TimeSpan.FromSeconds(60), what: "host self-checks finished");
            await _app.SettleAsync();
            Assert.True(_app.Vm.IsRoleIndicatorVisible, "the top-bar role indicator shows while hosting");
            _output.WriteLine($"host on: pin {network.PinDisplay}, addresses {string.Join(", ", network.Addresses)}, " +
                              $"checks {string.Join("; ", network.HostChecks.Select(c => $"{c.Key}={c.State}"))}");
            _output.WriteLine($"host on: {await _app.ScreenshotAsync("11-network-host-on")}");
        }
        finally
        {
            await _app.OnUiAsync(() => network.IsHostEnabled = false);
            await _app.WaitUntilAsync(() => !network.IsHostBusy && !network.IsHostRunning, what: "host stopped");
            await _app.SettleAsync();
        }

        Assert.False(_app.Vm.IsRoleIndicatorVisible);
        await _app.OnUiAsync(() => _app.Vm.CloseNetworkCommand.Execute(null));
        await _app.SettleAsync();
        Assert.False(_app.Vm.IsNetworkOpen);
        AssertNoUiErrors();
    }

    private void AssertNoUiErrors()
    {
        if (_app.UiErrors.Count == 0)
            return;
        var text = string.Join(Environment.NewLine, _app.UiErrors.Select(e => e.ToString()));
        _app.UiErrors.Clear();
        Assert.Fail($"The UI thread reported an exception:{Environment.NewLine}{text}");
    }
}
