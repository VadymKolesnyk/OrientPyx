using System.Collections.Concurrent;
using System.Net.Http.Json;
using OrientPyx.BusinessLogic.Serialization;
using OrientPyx.DataAccess.Network.Protocol;
using OrientPyx.DataAccess.Persistence;

namespace OrientPyx.DataAccess.Network.Station;

/// <summary>
/// Copies the station's activity log lines to the host (<c>POST /log</c>) in batches every two seconds, so the
/// competition's log on the host shows what every station did («[Mac-Реєстрація] …»). Best-effort: a failed batch is
/// dropped, nothing is logged about it (that would loop), and the queue is capped.
/// </summary>
public sealed class LogForwarder : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);
    private const int MaxQueued = 1000;

    private readonly FileActivityLog _log;
    private readonly RpcChannel _channel;
    private readonly Func<string?> _currentEvent;
    private readonly ConcurrentQueue<LogLine> _queue = new();
    private CancellationTokenSource? _cts;

    public LogForwarder(FileActivityLog log, RpcChannel channel, Func<string?> currentEvent)
    {
        _log = log;
        _channel = channel;
        _currentEvent = currentEvent;
    }

    public void Start()
    {
        if (_cts is not null)
            return;
        _cts = new CancellationTokenSource();
        _log.LineWritten += OnLine;
        _ = RunAsync(_cts.Token);
    }

    public void Stop()
    {
        _log.LineWritten -= OnLine;
        var cts = _cts;
        _cts = null;
        cts?.Cancel();
        cts?.Dispose();
    }

    private void OnLine(string level, string message)
    {
        if (_queue.Count >= MaxQueued)
            return;
        _queue.Enqueue(new LogLine(DateTimeOffset.Now, level, message));
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            await FlushAsync(cancellationToken);
        }
    }

    private async Task FlushAsync(CancellationToken cancellationToken)
    {
        if (_queue.IsEmpty)
            return;
        var lines = new List<LogLine>();
        while (lines.Count < 200 && _queue.TryDequeue(out var line))
            lines.Add(line);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Wire.LogPath)
            {
                Content = JsonContent.Create(new LogBatch(_currentEvent() ?? string.Empty, lines), options: RpcJson.Options),
            };
            using var response = await _channel.SendOnceAsync(request, RpcChannel.ReadTimeout, cancellationToken);
        }
        catch
        {
            // Dropped; the local file still has the lines.
        }
    }

    public void Dispose() => Stop();
}
