using OrientPyx.BusinessLogic.Interfaces;

namespace OrientPyx.BusinessLogic.Services.Mcp.Tools;

/// <summary>
/// The files the assistant may read and write (plan §6.5): a file to import is a local path the user named, with an
/// expected extension and at most 50 MB; an export goes to the competition's exports folder of this computer or to a
/// folder the user named (then the change is risky), never over an existing file without <c>overwrite</c>.
/// </summary>
internal static class McpFilePolicy
{
    public const long MaxImportBytes = 50L * 1024 * 1024;

    public static readonly IReadOnlyDictionary<string, string[]> ImportExtensions = new Dictionary<string, string[]>
    {
        ["iof_courses"] = [".xml"],
        ["iof_entries"] = [".xml"],
        ["uof"] = [".xml"],
        ["participants_table"] = [".csv", ".txt", ".xlsx"],
        ["rental_chips"] = [".csv", ".txt"],
        ["readouts"] = [".csv", ".txt"],
    };

    public static readonly HashSet<string> ExportExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".docx", ".html", ".htm", ".xml", ".csv", ".xlsx" };

    /// <summary>Reads a file to import after the checks; the bytes and the file name.</summary>
    public static async Task<(byte[] Bytes, string FileName)> ReadImportAsync(
        IMcpFileAccess files, string kind, string path, CancellationToken cancellationToken)
    {
        var full = Full(path);
        var ext = Path.GetExtension(full).ToLowerInvariant();
        if (!ImportExtensions.TryGetValue(kind, out var allowed) || !allowed.Contains(ext))
            throw new McpToolException($"A '{kind}' import takes {string.Join(", ", allowed ?? [])} files, not '{ext}'.");
        var size = files.SizeOf(full) ?? throw new McpToolException($"There is no file '{full}'. Ask the user for the exact path.");
        if (size > MaxImportBytes)
            throw new McpToolException($"The file is {size / 1024 / 1024} MB; at most 50 MB can be imported.");
        return (await files.ReadAsync(full, cancellationToken), Path.GetFileName(full));
    }

    /// <summary>Where an export goes; throws for an unexpected extension or an existing file without overwrite.</summary>
    public static string ExportPath(IMcpFileAccess files, string folder, string fileName, bool overwrite)
    {
        var name = Path.GetFileName(fileName.Trim());
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new McpToolException($"'{fileName}' is not a valid file name.");
        if (!ExportExtensions.Contains(Path.GetExtension(name)))
            throw new McpToolException($"An export is written only as {string.Join(", ", ExportExtensions)}.");
        var target = Path.Combine(Full(folder), name);
        if (files.Exists(target) && !overwrite)
            throw new McpToolException($"'{target}' already exists; pass overwrite=true to replace it, or another file_name.");
        return target;
    }

    public static string Full(string path)
    {
        var p = (path ?? string.Empty).Trim().Trim('"');
        if (p.Length == 0)
            throw new McpToolException("A path is required.");
        if (p.StartsWith("~/", StringComparison.Ordinal) || p == "~")
            p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), p.TrimStart('~', '/'));
        if (!Path.IsPathFullyQualified(p))
            throw new McpToolException($"'{path}' is not a full path. Ask the user for the full path of the file.");
        return Path.GetFullPath(p);
    }
}
