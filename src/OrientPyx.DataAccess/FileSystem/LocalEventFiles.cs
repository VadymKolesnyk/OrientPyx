using System.Security.Cryptography;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Services;

namespace OrientPyx.DataAccess.FileSystem;

/// <summary>
/// The local <see cref="IEventFiles"/>: the competition folder on this disk. Day folders are <c>day{N}</c> inside it;
/// read-out files and monitor screens default into the day folder (<c>chips/</c> for the rental chips), exactly as
/// before the network mode existed. Per-machine values are not needed locally (they stay in <c>event.db</c>).
/// </summary>
public sealed class LocalEventFiles : IEventFiles
{
    public Task EnsureDayFolderAsync(string eventFolderPath, int dayNumber, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(DayFolders.PathFor(eventFolderPath, dayNumber));
        return Task.CompletedTask;
    }

    public Task MoveDayFolderAsync(string eventFolderPath, int oldNumber, int newNumber, CancellationToken cancellationToken = default)
    {
        var oldFolder = DayFolders.PathFor(eventFolderPath, oldNumber);
        var newFolder = DayFolders.PathFor(eventFolderPath, newNumber);
        // Keep the day's files folder name in step with its number. The target name is free (its number was), so a
        // plain move works; if the old folder was never created, just create the new one so the day always has its
        // folder. A locked/again-renamed folder must not fail the renumber — the DB is the source of truth for the
        // number; the folder is just where imported files are kept.
        try
        {
            if (Directory.Exists(oldFolder) && !Directory.Exists(newFolder))
                Directory.Move(oldFolder, newFolder);
            else if (!Directory.Exists(newFolder))
                Directory.CreateDirectory(newFolder);
        }
        catch
        {
        }
        return Task.CompletedTask;
    }

    public Task DeleteDayFolderAsync(string eventFolderPath, int dayNumber, CancellationToken cancellationToken = default)
    {
        var folder = DayFolders.PathFor(eventFolderPath, dayNumber);
        try
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
        catch
        {
            // A file open elsewhere (the reader's CSV, a monitor screen in a browser) leaves the folder; the day is gone anyway.
        }
        return Task.CompletedTask;
    }

    public async Task<string> SaveDayFileAsync(
        string eventFolderPath, int dayNumber, string fileName, byte[] content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        var safeName = Path.GetFileName((fileName ?? string.Empty).Trim());
        if (string.IsNullOrEmpty(safeName))
            safeName = "import.xml";

        var folder = DayFolders.PathFor(eventFolderPath, dayNumber);
        Directory.CreateDirectory(folder);

        var target = Path.Combine(folder, safeName);

        // Identical bytes already there — reuse, don't duplicate.
        if (File.Exists(target) && SameContent(target, content))
            return safeName;

        if (!File.Exists(target))
        {
            await File.WriteAllBytesAsync(target, content, cancellationToken);
            return safeName;
        }

        // Name taken by different content → append a short content hash so both survive. If a file with that hashed
        // name is itself already identical (a re-import of the same file), reuse it.
        var stem = Path.GetFileNameWithoutExtension(safeName);
        var ext = Path.GetExtension(safeName);
        var hashedName = $"{stem}-{ShortHash(content)}{ext}";
        var hashedTarget = Path.Combine(folder, hashedName);

        if (File.Exists(hashedTarget) && SameContent(hashedTarget, content))
            return hashedName;

        await File.WriteAllBytesAsync(hashedTarget, content, cancellationToken);
        return hashedName;
    }

    public string MonitorOutputDirectory(string eventFolderPath, string identifier, int dayNumber)
        => DayFolders.PathFor(eventFolderPath, dayNumber);

    public string LocalReadoutDirectory(string eventFolderPath, string identifier, int? dayNumber)
        => dayNumber is { } n ? DayFolders.PathFor(eventFolderPath, n) : Path.Combine(eventFolderPath, "chips");

    public Task<string?> GetMachineValueAsync(string eventFolderPath, string key, CancellationToken cancellationToken = default)
        => Task.FromResult<string?>(null);

    public Task SetMachineValueAsync(string eventFolderPath, string key, string? value, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    private static bool SameContent(string path, byte[] content)
    {
        try
        {
            var existing = File.ReadAllBytes(path);
            return existing.AsSpan().SequenceEqual(content);
        }
        catch
        {
            // Unreadable existing file — treat as different so we fall through to a hashed copy.
            return false;
        }
    }

    private static string ShortHash(byte[] content)
    {
        var hash = SHA256.HashData(content);
        return Convert.ToHexString(hash.AsSpan(0, 4)).ToLowerInvariant();
    }
}
