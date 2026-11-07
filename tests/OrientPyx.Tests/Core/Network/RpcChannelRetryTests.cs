using System.Net;
using OrientPyx.BusinessLogic.Models.Network;
using OrientPyx.DataAccess.Network.Protocol;
using OrientPyx.DataAccess.Network.Station;

namespace OrientPyx.Tests.Core.Network;

/// <summary>
/// The station channel on a network that drops connections (a mobile router): a write retries through an outage with one
/// idempotency key and lands; writes go out in the order they were made even when the first one has to retry; a write
/// gives up only after its window and says so (<see cref="RpcChannel.WriteAbandoned"/>); a read whose attempt hangs on a
/// dead connection is retried after the short first timeout. Runs over a fake handler with short windows.
/// </summary>
public sealed class RpcChannelRetryTests
{
    private static readonly RpcRetryPolicy Fast = new(
        [TimeSpan.FromMilliseconds(200)], [TimeSpan.FromMilliseconds(20)], TimeSpan.FromSeconds(5));

    private static readonly RpcRetryPolicy Short = new(
        [TimeSpan.FromMilliseconds(100)], [TimeSpan.FromMilliseconds(20)], TimeSpan.FromMilliseconds(300));

    /// <summary>Answers each request with the callback (method name from the URL, 0-based attempt per method).</summary>
    private sealed class FakeHandler(Func<string, int, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer)
        : HttpMessageHandler
    {
        private readonly Dictionary<string, int> _attempts = [];
        private readonly object _gate = new();

        public List<(string Method, string? Key, bool Ok)> Log { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var method = request.RequestUri!.AbsolutePath.Split('/').Last();
            var key = request.Headers.TryGetValues(Wire.IdempotencyKeyHeader, out var values) ? values.Single() : null;
            int attempt;
            lock (_gate)
            {
                attempt = _attempts.GetValueOrDefault(method);
                _attempts[method] = attempt + 1;
            }
            try
            {
                var response = await answer(method, attempt, request, cancellationToken);
                lock (_gate)
                    Log.Add((method, key, true));
                return response;
            }
            catch
            {
                lock (_gate)
                    Log.Add((method, key, false));
                throw;
            }
        }
    }

    private static RpcChannel Channel(FakeHandler handler, RpcRetryPolicy? write = null)
        => new("127.0.0.1:1", handler, "token", readPolicy: Fast, writePolicy: write ?? Fast);

    private static HttpResponseMessage NoContent() => new(HttpStatusCode.NoContent);

    [Fact]
    public async Task Write_RetriesThroughAnOutage_WithOneIdempotencyKey()
    {
        var handler = new FakeHandler((_, attempt, _, _) => attempt < 2
            ? throw new HttpRequestException("No route to host")
            : Task.FromResult(NoContent()));
        using var channel = Channel(handler);

        await channel.CallAsync("EventStore", "SetParticipantsCheckedInAsync", ["KOSH-2026"], RpcCallKind.Write, typeof(void));

        Assert.Equal(3, handler.Log.Count);
        Assert.True(handler.Log[^1].Ok);
        var key = Assert.Single(handler.Log.Select(l => l.Key).Distinct());
        Assert.False(string.IsNullOrEmpty(key));
        Assert.Equal(0, channel.Health.DelayedWrites);
        Assert.False(channel.Health.IsOffline);
    }

    [Fact]
    public async Task Writes_GoOutInTheOrderTheyWereMade()
    {
        var firstFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakeHandler(async (method, attempt, _, _) =>
        {
            if (method == "First" && attempt == 0)
            {
                await Task.Delay(50);
                firstFailed.TrySetResult();
                throw new HttpRequestException("Connection reset");
            }
            return NoContent();
        });
        using var channel = Channel(handler);

        var first = channel.CallAsync("EventStore", "First", [], RpcCallKind.Write, typeof(void));
        var second = channel.CallAsync("EventStore", "Second", [], RpcCallKind.Write, typeof(void));
        await Task.WhenAll(first, second);

        Assert.Equal(
            new[] { ("First", false), ("First", true), ("Second", true) },
            handler.Log.Select(l => (l.Method, l.Ok)).ToArray());
    }

    [Fact]
    public async Task Write_GivesUpAfterItsWindow_AndSaysSo()
    {
        var handler = new FakeHandler((_, _, _, _) => throw new HttpRequestException("Network is unreachable"));
        using var channel = Channel(handler, write: Short);
        var abandoned = new List<string>();
        channel.WriteAbandoned += (call, _) => abandoned.Add(call);

        await Assert.ThrowsAsync<HostUnreachableException>(() =>
            channel.CallAsync("EventStore", "UpdateParticipantAsync", [], RpcCallKind.Write, typeof(void)));

        Assert.True(handler.Log.Count > 1, "the write must have been retried");
        Assert.Equal(new[] { "EventStore.UpdateParticipantAsync" }, abandoned);
        Assert.Equal(0, channel.Health.DelayedWrites);
    }

    [Fact]
    public async Task Read_StuckOnADeadConnection_IsRetried()
    {
        var handler = new FakeHandler(async (_, attempt, _, cancellationToken) =>
        {
            if (attempt == 0)
                await Task.Delay(Timeout.Infinite, cancellationToken); // a connection the router dropped: no answer, ever
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("42", System.Text.Encoding.UTF8, Wire.JsonContentType) };
        });
        using var channel = Channel(handler);

        var value = await channel.CallAsync("EventStore", "CountSomethingAsync", [], RpcCallKind.Read, typeof(int));

        Assert.Equal(42, value);
        Assert.Equal(new[] { false, true }, handler.Log.Select(l => l.Ok).ToArray());
        Assert.All(handler.Log, l => Assert.Null(l.Key)); // reads carry no key
    }
}
