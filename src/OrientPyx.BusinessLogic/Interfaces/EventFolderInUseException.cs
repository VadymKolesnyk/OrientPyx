namespace OrientPyx.BusinessLogic.Interfaces;

/// <summary>
/// Thrown when an import would replace a competition folder that is still held open (typically by another
/// running OrientPyx instance). The existing competition is left untouched.
/// </summary>
public sealed class EventFolderInUseException : Exception
{
    public EventFolderInUseException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
