namespace OrientDesk.BusinessLogic.Interfaces;

/// <summary>
/// A lightweight, app-wide diagnostic log. One file is created per application launch under the
/// events folder's <c>logs</c> directory; every user action and every exception is appended to it.
/// Implemented in DataAccess (file I/O lives there). All methods are best-effort and must never throw.
/// </summary>
public interface IActivityLog
{
    /// <summary>Records a user action (button click, edit, navigation, import, …).</summary>
    void Action(string message);

    /// <summary>Records an informational/diagnostic line.</summary>
    void Info(string message);

    /// <summary>Records an exception together with a short context describing where it happened.</summary>
    void Error(string context, Exception exception);

    /// <summary>Absolute path of the current launch's log file, for surfacing to the user.</summary>
    string LogFilePath { get; }
}
