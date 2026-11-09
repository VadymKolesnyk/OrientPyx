using System.Text.Json;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.DataAccess.Persistence;

namespace OrientPyx.DataAccess.Network.Station;

/// <summary>
/// The station's <see cref="IEventFiles"/>: the competition folder's files (day folders, imported XML) go to the host
/// over RPC; the paths of this machine's own files — read-out files, monitor HTML, the per-competition
/// <c>machine.json</c> — live under the data root, keyed by host and identifier, because a reader or a screen is
/// plugged into <b>this</b> computer (П4, П17).
/// </summary>
public sealed class RemoteEventFiles : IEventFiles
{
    private const string MachineFileName = "machine.json";

    private readonly IEventFiles _remote;
    private readonly string _hostId;
    private readonly SemaphoreSlim _machineGate = new(1, 1);

    public RemoteEventFiles(RpcChannel channel, string hostId)
    {
        _remote = RpcProxy<IEventFiles>.Create(channel);
        _hostId = Safe(hostId);
    }

    public Task EnsureDayFolderAsync(string eventFolderPath, int dayNumber, CancellationToken cancellationToken = default)
        => _remote.EnsureDayFolderAsync(eventFolderPath, dayNumber, cancellationToken);

    public Task MoveDayFolderAsync(string eventFolderPath, int oldNumber, int newNumber, CancellationToken cancellationToken = default)
        => _remote.MoveDayFolderAsync(eventFolderPath, oldNumber, newNumber, cancellationToken);

    public Task DeleteDayFolderAsync(string eventFolderPath, int dayNumber, CancellationToken cancellationToken = default)
        => _remote.DeleteDayFolderAsync(eventFolderPath, dayNumber, cancellationToken);

    public Task<string> SaveDayFileAsync(string eventFolderPath, int dayNumber, string fileName, byte[] content, CancellationToken cancellationToken = default)
        => _remote.SaveDayFileAsync(eventFolderPath, dayNumber, fileName, content, cancellationToken);

    public string MonitorOutputDirectory(string eventFolderPath, string identifier, int dayNumber)
        => Ensure(Path.Combine(AppDatabasePaths.BaseDirectory, "monitor", Safe(Key(eventFolderPath, identifier)), $"day{dayNumber}"));

    public string LocalReadoutDirectory(string eventFolderPath, string identifier, int? dayNumber)
        => Ensure(Path.Combine(LocalFolder(eventFolderPath, identifier), dayNumber is { } n ? $"day{n}" : "chips"));

    public async Task<string?> GetMachineValueAsync(string eventFolderPath, string key, CancellationToken cancellationToken = default)
    {
        await _machineGate.WaitAsync(cancellationToken);
        try
        {
            var values = await ReadMachineAsync(eventFolderPath, cancellationToken);
            return values.TryGetValue(key, out var value) ? value : null;
        }
        finally
        {
            _machineGate.Release();
        }
    }

    public async Task SetMachineValueAsync(string eventFolderPath, string key, string? value, CancellationToken cancellationToken = default)
    {
        await _machineGate.WaitAsync(cancellationToken);
        try
        {
            var values = await ReadMachineAsync(eventFolderPath, cancellationToken);
            if (value is null)
                values.Remove(key);
            else
                values[key] = value;
            var file = Path.Combine(Ensure(LocalFolder(eventFolderPath, null)), MachineFileName);
            await File.WriteAllTextAsync(file, JsonSerializer.Serialize(values, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
        }
        finally
        {
            _machineGate.Release();
        }
    }

    private async Task<Dictionary<string, string>> ReadMachineAsync(string eventFolderPath, CancellationToken cancellationToken)
    {
        var file = Path.Combine(LocalFolder(eventFolderPath, null), MachineFileName);
        if (!File.Exists(file))
            return new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var json = await File.ReadAllTextAsync(file, cancellationToken);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    // <data root>/local/<hostId>/<identifier>
    private string LocalFolder(string eventFolderPath, string? identifier)
        => Path.Combine(AppDatabasePaths.BaseDirectory, "local", _hostId, Safe(Key(eventFolderPath, identifier)));

    // On a station the folder path IS the identifier; fall back to the explicit identifier when given.
    private static string Key(string eventFolderPath, string? identifier)
    {
        var fromPath = Path.GetFileName((eventFolderPath ?? string.Empty).TrimEnd('/', '\\'));
        return string.IsNullOrEmpty(fromPath) ? identifier ?? "_" : fromPath;
    }

    private static string Ensure(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
        }
        catch
        {
            // The caller gets the path either way; a failed write reports its own error.
        }
        return path;
    }

    private static string Safe(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = (value ?? string.Empty).Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return chars.Length == 0 ? "_" : new string(chars);
    }
}
