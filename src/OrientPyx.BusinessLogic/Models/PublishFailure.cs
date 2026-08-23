namespace OrientPyx.BusinessLogic.Models;

/// <summary>
/// Why a publish attempt failed, in terms a competition secretary can act on. The publisher classifies the
/// raw transport/HTTP failure into one of these; the UI maps the code onto a localized sentence, so the log
/// says "немає інтернету" instead of .NET's opaque "An error occurred while sending the request."
/// </summary>
public enum PublishFailureKind
{
    /// <summary>Anything that didn't fit the cases below — show the raw detail.</summary>
    Unknown,

    /// <summary>The host name didn't resolve — no internet, or DNS is down.</summary>
    NoDns,

    /// <summary>TCP/TLS couldn't be established or was torn down mid-request — network dropped.</summary>
    NoConnection,

    /// <summary>The request took longer than the client timeout.</summary>
    Timeout,

    /// <summary>The server rejected the credentials (401/403) — wrong or expired service-role key.</summary>
    Unauthorized,

    /// <summary>The server accepted the request but reported an error (5xx) — Supabase-side problem.</summary>
    ServerError,

    /// <summary>The request was malformed or violated a constraint (4xx other than auth) — a schema mismatch.</summary>
    BadRequest,
}

/// <summary>
/// A publish attempt that failed, carrying both a coarse <see cref="Kind"/> the UI can turn into a plain-language
/// message and the underlying <see cref="Detail"/> for the log. Thrown by <c>IResultPublisher.PublishAsync</c>.
/// </summary>
public sealed class PublishException : Exception
{
    public PublishException(PublishFailureKind kind, string detail, Exception? inner = null)
        : base(detail, inner)
    {
        Kind = kind;
        Detail = detail;
    }

    /// <summary>The classified reason, used to pick the user-facing sentence.</summary>
    public PublishFailureKind Kind { get; }

    /// <summary>The technical detail (inner exception chain, or the server's response body).</summary>
    public string Detail { get; }
}
