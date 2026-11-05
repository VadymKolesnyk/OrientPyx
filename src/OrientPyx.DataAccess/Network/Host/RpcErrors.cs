using System.Reflection;
using System.Text.Json;
using OrientPyx.BusinessLogic.Interfaces;
using OrientPyx.BusinessLogic.Models;
using OrientPyx.BusinessLogic.Serialization;
using OrientPyx.DataAccess.Network.Protocol;

namespace OrientPyx.DataAccess.Network.Host;

/// <summary>Maps an exception thrown on the host onto the HTTP status and <see cref="RpcError"/> body a station gets
/// (see <c>docs/network-protocol.md</c>, «Помилки»).</summary>
internal static class RpcErrors
{
    public static (int Status, RpcError Error) Map(Exception exception)
    {
        if (exception is TargetInvocationException { InnerException: { } inner })
            exception = inner;

        var type = exception.GetType().FullName ?? exception.GetType().Name;
        var message = exception.Message;
        return exception switch
        {
            DayLockedException d => (409, new RpcError(type, message, RpcErrorKinds.Conflict, d.DayNumber)),
            EventArchiveFormatException => (400, new RpcError(type, message, RpcErrorKinds.BadRequest)),
            EventFolderInUseException => (409, new RpcError(type, message, RpcErrorKinds.Conflict)),
            ArgumentException => (400, new RpcError(type, message, RpcErrorKinds.BadRequest)),
            KeyNotFoundException => (404, new RpcError(type, message, RpcErrorKinds.NotFound)),
            InvalidOperationException => (409, new RpcError(type, message, RpcErrorKinds.Conflict)),
            OperationCanceledException => (Wire.ClientClosedRequestStatus, new RpcError(type, message, RpcErrorKinds.Cancelled)),
            _ => (500, new RpcError(type, message, RpcErrorKinds.Error)),
        };
    }

    public static RpcOutcome Outcome(Exception exception)
    {
        var (status, error) = Map(exception);
        return new RpcOutcome(status, Serialize(error));
    }

    public static RpcOutcome Outcome(int status, string kind, string message, string? type = null)
        => new(status, Serialize(new RpcError(type ?? kind, message, kind)));

    public static byte[] Serialize(RpcError error) => JsonSerializer.SerializeToUtf8Bytes(error, RpcJson.Options);
}
