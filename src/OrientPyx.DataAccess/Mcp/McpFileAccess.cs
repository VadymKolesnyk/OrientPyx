using OrientPyx.BusinessLogic.Interfaces;

namespace OrientPyx.DataAccess.Mcp;

/// <summary>The local file access of the assistant (BusinessLogic checks the paths first, <c>McpFilePolicy</c>).</summary>
public sealed class McpFileAccess : IMcpFileAccess
{
    public long? SizeOf(string fullPath)
    {
        var info = new FileInfo(fullPath);
        return info.Exists ? info.Length : null;
    }

    public Task<byte[]> ReadAsync(string fullPath, CancellationToken cancellationToken = default) =>
        File.ReadAllBytesAsync(fullPath, cancellationToken);

    public bool Exists(string fullPath) => File.Exists(fullPath);

    public async Task WriteAsync(string fullPath, byte[] content, CancellationToken cancellationToken = default)
    {
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(fullPath, content, cancellationToken);
    }
}
