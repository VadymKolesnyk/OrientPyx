namespace OrientPyx.BusinessLogic.Interfaces;

/// <summary>
/// The files of a competition folder that BusinessLogic needs besides the database: the per-day folders and the
/// archive of imported files (on a station those live on the host and go over the network), plus the paths of files
/// that belong to <b>this machine</b> — a read-out file lies where the reader is plugged in, the monitor HTML where the
/// screen is — which are always local in both roles.
/// </summary>
public interface IEventFiles
{
    // ── The competition folder (on a station: on the host)

    /// <summary>Creates the day's folder (<c>day{N}</c>) if missing.</summary>
    Task EnsureDayFolderAsync(string eventFolderPath, int dayNumber, CancellationToken cancellationToken = default);

    /// <summary>Renames a day's folder after its number changed; creates the new one when the old never existed.
    /// Best-effort: a locked folder does not fail the renumbering.</summary>
    Task MoveDayFolderAsync(string eventFolderPath, int oldNumber, int newNumber, CancellationToken cancellationToken = default);

    /// <summary>Deletes a deleted day's folder (<c>day{N}</c>) with its files, so a later day with that number starts
    /// empty. Best-effort: a locked file does not fail the day's deletion.</summary>
    Task DeleteDayFolderAsync(string eventFolderPath, int dayNumber, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores a file imported for a day (an IOF XML) in the day's folder: identical content is reused, a name clash
    /// with different content gets a short content hash appended. Returns the stored file name.
    /// </summary>
    Task<string> SaveDayFileAsync(string eventFolderPath, int dayNumber, string fileName, byte[] content, CancellationToken cancellationToken = default);

    // ── Local paths of this machine (executed locally in every role)

    /// <summary>Where the results-monitor HTML screens of a day are written on this machine.</summary>
    string MonitorOutputDirectory(string eventFolderPath, string identifier, int dayNumber);

    /// <summary>Where the read-out files of a day (<c>event.csv</c>) or, with a null day, of the rental chips
    /// (<c>chips/rentchip.csv</c>) are expected on this machine.</summary>
    string LocalReadoutDirectory(string eventFolderPath, string identifier, int? dayNumber);

    /// <summary>A per-machine value tied to a competition (e.g. the chip-scan file path on a station); null when unset.
    /// Locally the values stay in <c>event.db</c> and this returns null.</summary>
    Task<string?> GetMachineValueAsync(string eventFolderPath, string key, CancellationToken cancellationToken = default);

    Task SetMachineValueAsync(string eventFolderPath, string key, string? value, CancellationToken cancellationToken = default);
}
