using System.Net.Http.Headers;
using System.Net.Http.Json;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.BusinessLogic.Serialization;
using OrientPyx.DataAccess.Network.Protocol;

namespace OrientPyx.DataAccess.Network.Station;

/// <summary>
/// The station's <see cref="IEventArchiveService"/> (П23): the archive file lives on this machine, the competition on
/// the host, so export downloads the <c>.opyx</c> the host builds (<c>GET /files/archive</c>) and preview/import
/// upload the local file (<c>POST /files/preview</c>, <c>POST /files/import</c>). The identifier checks are local
/// (pure) or one RPC.
/// </summary>
public sealed class RemoteArchiveService : IEventArchiveService
{
    private static readonly TimeSpan TransferTimeout = TimeSpan.FromMinutes(10);

    private readonly RpcChannel _channel;
    private readonly IEventArchiveService _rpc;

    public RemoteArchiveService(RpcChannel channel)
    {
        _channel = channel;
        _rpc = RpcProxy<IEventArchiveService>.Create(channel);
    }

    public bool IsValidIdentifier(string identifier) => EventIdentifier.IsValid(identifier);

    public Task<bool> IsIdentifierAvailableAsync(string identifier, CancellationToken cancellationToken = default)
        => _rpc.IsIdentifierAvailableAsync(identifier, cancellationToken);

    public async Task ExportAsync(string identifier, string destinationArchivePath, CancellationToken cancellationToken = default)
    {
        identifier = (identifier ?? string.Empty).Trim();
        if (!EventIdentifier.IsValid(identifier))
            throw new ArgumentException("Identifier must be a valid folder name.", nameof(identifier));

        var request = new HttpRequestMessage(HttpMethod.Get, $"{Wire.ArchiveDownloadPath}?identifier={Uri.EscapeDataString(identifier)}");
        using var response = await _channel.SendOnceAsync(request, TransferTimeout, cancellationToken);
        await _channel.ThrowOnErrorAsync(response, cancellationToken);

        var temp = destinationArchivePath + ".part";
        try
        {
            await using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            await using (var body = await response.Content.ReadAsStreamAsync(cancellationToken))
                await body.CopyToAsync(file, cancellationToken);
            File.Move(temp, destinationArchivePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    public async Task<EventArchivePreview> PreviewImportAsync(string archivePath, CancellationToken cancellationToken = default)
    {
        using var response = await UploadAsync(Wire.ArchivePreviewPath, archivePath, cancellationToken);
        return await response.Content.ReadFromJsonAsync<EventArchivePreview>(RpcJson.Options, cancellationToken)
               ?? throw new EventArchiveFormatException("The host returned no preview.");
    }

    public async Task<EventArchiveImportResult> ImportAsync(string archivePath, string identifier, bool overwrite, CancellationToken cancellationToken = default)
    {
        identifier = (identifier ?? string.Empty).Trim();
        if (!EventIdentifier.IsValid(identifier))
            throw new ArgumentException("Identifier must be a valid folder name.", nameof(identifier));

        var path = $"{Wire.ArchiveUploadPath}?identifier={Uri.EscapeDataString(identifier)}&overwrite={(overwrite ? "true" : "false")}";
        using var response = await UploadAsync(path, archivePath, cancellationToken);
        return await response.Content.ReadFromJsonAsync<EventArchiveImportResult>(RpcJson.Options, cancellationToken)
               ?? throw new EventArchiveFormatException("The host returned no import result.");
    }

    private async Task<HttpResponseMessage> UploadAsync(string path, string archivePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(archivePath))
            throw new FileNotFoundException("Archive not found.", archivePath);

        await using var file = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StreamContent(file) { Headers = { ContentType = new MediaTypeHeaderValue(Wire.OctetStreamContentType) } },
        };
        var response = await _channel.SendOnceAsync(request, TransferTimeout, cancellationToken);
        try
        {
            await _channel.ThrowOnErrorAsync(response, cancellationToken);
        }
        catch
        {
            response.Dispose();
            throw;
        }
        return response;
    }
}
