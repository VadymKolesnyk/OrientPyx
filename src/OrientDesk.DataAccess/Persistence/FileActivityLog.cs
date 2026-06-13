using System.Text;
using OrientDesk.BusinessLogic.Interfaces;

namespace OrientDesk.DataAccess.Persistence;

/// <summary>
/// Writes a per-launch diagnostic log file under <c>&lt;events&gt;/logs/</c>. Each launch gets its own
/// timestamped <c>.log</c> file; actions, info lines and exceptions are appended as they happen.
/// Best-effort: every method swallows its own I/O errors so logging can never break the app.
/// </summary>
public sealed class FileActivityLog : IActivityLog
{
    private readonly object _gate = new();
    private readonly string _logFilePath;

    public FileActivityLog()
    {
        // One file per launch, named by start time; collisions across fast restarts get a suffix.
        var logsDir = Path.Combine(AppDatabasePaths.DefaultEventsPath, "logs");
        var stamp = DateTimeOffset.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        var path = Path.Combine(logsDir, $"{stamp}.log");
        try
        {
            Directory.CreateDirectory(logsDir);
            var n = 1;
            while (File.Exists(path))
                path = Path.Combine(logsDir, $"{stamp}_{n++}.log");
        }
        catch
        {
            // If the directory can't be created, Append below will just keep failing quietly.
        }
        _logFilePath = path;

        Write("INFO", $"=== OrientDesk session started ({DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}) ===");
    }

    public string LogFilePath => _logFilePath;

    public void Action(string message) => Write("ACTION", message);

    public void Info(string message) => Write("INFO", message);

    public void Error(string context, Exception exception)
    {
        var sb = new StringBuilder();
        sb.Append(context);
        sb.Append(" — ");
        sb.AppendLine(exception.GetType().Name + ": " + exception.Message);
        sb.Append(exception);
        Write("ERROR", sb.ToString());
    }

    private void Write(string level, string message)
    {
        var line = $"[{DateTimeOffset.Now:HH:mm:ss.fff}] {level,-6} {message}{Environment.NewLine}";
        try
        {
            lock (_gate)
                File.AppendAllText(_logFilePath, line);
        }
        catch
        {
            // Logging must never throw.
        }
    }
}
