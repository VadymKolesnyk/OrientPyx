using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Services.Network;
using OrientPyx.DataAccess.Network.Protocol;
using OrientPyx.DataAccess.Network.Station;
using OrientPyx.Tests.Support;

namespace OrientPyx.Tests.Core.Network;

/// <summary>
/// The host's <c>/changes</c> over the loopback, read raw: it announces its heartbeat and its epoch, gives a fresh
/// connection its position (<c>id:</c> before <c>: connected</c>), resumes a reconnect of the same host run from
/// <c>Last-Event-ID</c> without a full refresh, and answers a reconnect carrying another run's epoch with
/// <c>full-refresh</c> — so a station whose stream a router dropped picks up exactly where it was.
/// </summary>
[Collection(AppCollection.Name)]
public sealed class ChangeStreamTests : IClassFixture<LoopbackHostFixture>
{
    private static readonly TimeSpan LineTimeout = TimeSpan.FromSeconds(10);

    private readonly LoopbackHostFixture _f;

    public ChangeStreamTests(LoopbackHostFixture fixture) => _f = fixture;

    private IDataChangeBus HostBus => _f.HostServices.GetRequiredService<IDataChangeBus>();

    private sealed class SseConnection(HttpClient http, HttpResponseMessage response, StreamReader reader) : IDisposable
    {
        public HttpResponseMessage Response => response;

        public string Header(string name) => response.Headers.GetValues(name).Single();

        /// <summary>The lines up to and including <paramref name="last"/>.</summary>
        public async Task<List<string>> ReadUntilAsync(Func<string, bool> last)
        {
            var lines = new List<string>();
            while (true)
            {
                using var cts = new CancellationTokenSource(LineTimeout);
                var line = await reader.ReadLineAsync(cts.Token) ?? throw new IOException("stream closed");
                lines.Add(line);
                if (last(line))
                    return lines;
            }
        }

        public void Dispose()
        {
            reader.Dispose();
            response.Dispose();
            http.Dispose();
        }
    }

    private async Task<SseConnection> OpenAsync(long? lastEventId = null, string? epoch = null)
    {
        var http = new HttpClient { BaseAddress = RpcChannel.BaseUri(_f.Endpoint), Timeout = Timeout.InfiniteTimeSpan };
        using var request = new HttpRequestMessage(HttpMethod.Get, Wire.ChangesPath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _f.Station.Channel!.Token);
        if (lastEventId is { } id)
            request.Headers.TryAddWithoutValidation(Wire.LastEventIdHeader, id.ToString());
        if (epoch is not null)
            request.Headers.TryAddWithoutValidation(Wire.StreamEpochHeader, epoch);
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        return new SseConnection(http, response, reader);
    }

    private static long BaselineOf(List<string> lines)
        => long.Parse(Assert.Single(lines, l => l.StartsWith("id:", StringComparison.Ordinal))[3..].Trim());

    [Fact]
    public async Task FreshConnection_AnnouncesHeartbeatEpochAndPosition()
    {
        using var stream = await OpenAsync();

        Assert.Equal("3", stream.Header(Wire.HeartbeatHeader));
        Assert.False(string.IsNullOrEmpty(stream.Header(Wire.StreamEpochHeader)));
        var lines = await stream.ReadUntilAsync(l => l == ": connected");
        Assert.True(BaselineOf(lines) >= 0);
        Assert.DoesNotContain(lines, l => l.Contains(Wire.FullRefreshEvent));
    }

    [Fact]
    public async Task ReconnectOfTheSameHostRun_ResumesWithoutFullRefresh()
    {
        string epoch;
        long position;
        using (var first = await OpenAsync())
        {
            epoch = first.Header(Wire.StreamEpochHeader);
            position = BaselineOf(await first.ReadUntilAsync(l => l == ": connected"));
        }

        // Missed while the station's stream was down.
        var missed = HostBus.Publish(DataChangeBus.AnyEvent, DataArea.AppData, "test");

        using var again = await OpenAsync(position, epoch);
        var lines = await again.ReadUntilAsync(l => l == ": connected");
        Assert.Contains($"id: {missed.Sequence}", lines);
        Assert.Contains($"event: {Wire.ChangeEvent}", lines);
        Assert.DoesNotContain(lines, l => l.Contains(Wire.FullRefreshEvent));
    }

    [Fact]
    public async Task ReconnectFromAnotherHostRun_GetsFullRefreshAndANewPosition()
    {
        using var stream = await OpenAsync(lastEventId: 5, epoch: "an-older-run-of-the-host");

        var lines = await stream.ReadUntilAsync(l => l == ": connected");
        Assert.Equal($"event: {Wire.FullRefreshEvent}", lines[0]);
        Assert.InRange(BaselineOf(lines), 0, HostBus.Sequence);
    }
}
