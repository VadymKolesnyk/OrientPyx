namespace OrientPyx.BusinessLogic.Interfaces;

/// <summary>
/// The local files the assistant imports from and exports to (files of THIS computer — on a station too). BusinessLogic
/// decides which paths are allowed (<c>McpFilePolicy</c>); the reading and writing lives in DataAccess.
/// </summary>
public interface IMcpFileAccess
{
    /// <summary>The size of a file, or null when there is none.</summary>
    long? SizeOf(string fullPath);

    Task<byte[]> ReadAsync(string fullPath, CancellationToken cancellationToken = default);

    bool Exists(string fullPath);

    /// <summary>Writes the file (creating its folder).</summary>
    Task WriteAsync(string fullPath, byte[] content, CancellationToken cancellationToken = default);
}
