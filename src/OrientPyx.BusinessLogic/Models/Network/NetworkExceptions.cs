namespace OrientPyx.BusinessLogic.Models.Network;

/// <summary>A station's call to the host failed after every retry — the host is not reachable right now.</summary>
public sealed class HostUnreachableException : Exception
{
    public HostUnreachableException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>The host executed the call and it threw an exception the station cannot map onto a local type.</summary>
public sealed class RemoteCallException : Exception
{
    public RemoteCallException(string remoteType, string message) : base(message) => RemoteType = remoteType;

    /// <summary>The full name of the exception type thrown on the host.</summary>
    public string RemoteType { get; }
}

/// <summary>The host refused the station: wrong PIN, incompatible protocol, revoked token.</summary>
public sealed class PairingRefusedException : Exception
{
    public PairingRefusedException(string reasonKey, string message) : base(message) => ReasonKey = reasonKey;

    /// <summary>A localisation key suffix (<c>Network.Error.&lt;ReasonKey&gt;</c>).</summary>
    public string ReasonKey { get; }
}
