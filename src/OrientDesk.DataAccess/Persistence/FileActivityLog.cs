using System.Text;
using OrientDesk.BusinessLogic.Interfaces;

namespace OrientDesk.DataAccess.Persistence;

/// <summary>
/// Writes a per-launch diagnostic log file. Before a competition is selected this is the shared
/// <c>&lt;events&gt;/logs/</c> folder; once <see cref="UseEventFolder"/> is called the log moves into
/// the selected competition's own <c>&lt;event&gt;/logs/</c> folder. Each launch gets its own
/// timestamped <c>.log</c> file; actions, info lines and exceptions are appended as they happen.
/// Best-effort: every method swallows its own I/O errors so logging can never break the app.
/// </summary>
public sealed class FileActivityLog : IActivityLog
{
    private readonly object _gate = new();
    private readonly string _stamp;
    private string _logFilePath;

    public FileActivityLog()
    {
        // One file per launch, named by start time; collisions across fast restarts get a suffix.
        _stamp = DateTimeOffset.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        _logFilePath = OpenIn(Path.Combine(AppDatabasePaths.DefaultEventsPath, "logs"));

        Write("INFO", $"=== OrientDesk session started ({DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}) ===");
    }

    public string LogFilePath
    {
        get { lock (_gate) return _logFilePath; }
    }

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

    public void UseEventFolder(string eventFolderPath)
    {
        if (string.IsNullOrWhiteSpace(eventFolderPath))
            return;

        var target = OpenIn(Path.Combine(eventFolderPath, "logs"));
        lock (_gate)
        {
            if (string.Equals(target, _logFilePath, StringComparison.OrdinalIgnoreCase))
                return; // already logging into this competition's folder
            _logFilePath = target;
        }

        Write("INFO", $"=== Logging moved to competition folder ({DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}) ===");
    }

    // Resolves a free, launch-stamped log file path inside the given folder and ensures the folder
    // exists. Never throws — if the folder can't be made, Append below just keeps failing quietly.
    private string OpenIn(string logsDir)
    {
        var path = Path.Combine(logsDir, $"{_stamp}.log");
        try
        {
            Directory.CreateDirectory(logsDir);
            var n = 1;
            while (File.Exists(path))
                path = Path.Combine(logsDir, $"{_stamp}_{n++}.log");
        }
        catch
        {
            // Leave the candidate path as-is; writes will fail quietly.
        }
        return path;
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
