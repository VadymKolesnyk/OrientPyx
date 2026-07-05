namespace OrientPyx.BusinessLogic.Interfaces;

/// <summary>Thrown when a file offered for import is not a valid competition archive.</summary>
public sealed class EventArchiveFormatException : Exception
{
    public EventArchiveFormatException(string message) : base(message)
    {
    }
}
