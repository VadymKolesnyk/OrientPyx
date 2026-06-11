using System.IO;

namespace OrientDesk.Presentation.Services;

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
    // fight it for a lock. A missing/locked file yields null and the loop retries next tick.
    private static async Task<string?> TryReadAsync(string filePath, CancellationToken token)
    {
        try
        {
            await using var stream = new FileStream(
                filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync(token);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }
}
