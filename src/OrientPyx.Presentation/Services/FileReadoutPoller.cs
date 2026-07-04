using System.IO;

namespace OrientPyx.Presentation.Services;

/// <summary>
/// Default <see cref="IFileReadoutPoller"/>. Runs a single background loop (a
/// <see cref="CancellationTokenSource"/> plus <c>Task.Delay</c>, the same shape as the grids'
/// debounced save) that reads the file and awaits the callback, then waits the interval. No timer,
/// no hosted service, no extra dependency.
/// </summary>
public sealed class FileReadoutPoller : IFileReadoutPoller
{
    private CancellationTokenSource? _cts;

    public void Start(string filePath, TimeSpan interval, Func<string, Task> onContent)
    {
        ArgumentNullException.ThrowIfNull(onContent);

        Stop();

        // Floor the interval so a 0 (or tiny) value can't spin the loop.
        if (interval < TimeSpan.FromSeconds(1))
            interval = TimeSpan.FromSeconds(1);

        EnsureFileExists(filePath);

        var cts = new CancellationTokenSource();
        _cts = cts;
        _ = RunAsync(filePath, interval, onContent, cts.Token);
    }

    // Creates the file (and any missing parent folders) so the SI software can write to it and the
    // poll has something to read. An empty file parses to no chips, which is the correct start state.
    // Best-effort: a failure here just means the loop reads nothing until the file appears.
    private static void EnsureFileExists(string filePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(filePath) || File.Exists(filePath))
                return;

            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            using (File.Create(filePath)) { }
        }
        catch
        {
            // Could not create it (bad path, permissions); the poll will simply find nothing to read.
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    private static async Task RunAsync(
        string filePath,
        TimeSpan interval,
        Func<string, Task> onContent,
        CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var content = await TryReadAsync(filePath, token);
                if (content is not null)
                    await onContent(content);

                await Task.Delay(interval, token);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped (or replaced) — expected.
        }
        catch
        {
            // The loop must never crash the app; a failing read/callback just ends this watch.
        }
    }

    // Reads the file allowing concurrent writers (the SI software keeps the log open), so we never
    // fight it for a lock. We share read AND write so an open that keeps the file with FileShare.Read
    // doesn't lock us out. Some writers, though, briefly hold the file *exclusively* (FileShare.None)
    // while flushing a record — that open throws an IOException and would drop the whole tick. So we
    // retry the open a few times with a short backoff inside the tick before giving up; a still-locked
    // or missing file then yields null and the loop retries next tick.
    private const int OpenAttempts = 5;
    private static readonly TimeSpan OpenRetryDelay = TimeSpan.FromMilliseconds(100);

    private static async Task<string?> TryReadAsync(string filePath, CancellationToken token)
    {
        for (var attempt = 0; attempt < OpenAttempts; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                await using var stream = new FileStream(
                    filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, token);
                // A readout file has no in-band encoding: SPORTident exports are UTF-8, Sport Time is
                // windows-1251. Decode via the shared reader so either format reads correctly regardless
                // of which timing system the operator selected.
                return CsvEncodingReader.Decode(buffer.ToArray());
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (IOException) when (attempt < OpenAttempts - 1)
            {
                // Most likely a transient exclusive lock while the writer flushes — wait and retry.
                await Task.Delay(OpenRetryDelay, token);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }
}
