using System.Text;

namespace Gravityless.Core.Services;

/// <summary>
/// Lightweight diagnostic log for failures that are swallowed by design.
///
/// Many call sites intentionally ignore errors: cleaning up a temp file, probing whether a
/// path exists, resetting a process handle. Catching and discarding is correct there, but
/// it leaves no trace when the ignored failure actually matters. Recording them in one
/// rolling file keeps the code silent without making problems invisible.
///
/// Writes are best-effort and never throw: a logging failure must not become the bug.
/// </summary>
public static class DiagLog
{
    private static readonly object Gate = new();
    private static readonly string LogPath = ResolveLogPath();
    private const long MaxBytes = 2 * 1024 * 1024;

    /// <summary>True to write trace entries. Off unless explicitly enabled.</summary>
    public static bool Verbose { get; set; }

    private static string ResolveLogPath()
    {
        try
        {
            var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "logs");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "diagnostics.log");
        }
        catch
        {
            return Path.Combine(Path.GetTempPath(), "gravityless-diagnostics.log");
        }
    }

    /// <summary>Records a swallowed exception against the calling site.</summary>
    /// <param name="context">Short tag identifying where the failure occurred.</param>
    /// <param name="ex">The exception that was discarded.</param>
    public static void Swallowed(string context, Exception? ex)
    {
        if (ex == null) return;
        Write(context, $"{ex.GetType().Name}: {ex.Message}");
    }

    /// <summary>Records a plain note. Only written when <see cref="Verbose"/> is set.</summary>
    public static void Trace(string context, string message)
    {
        if (!Verbose) return;
        Write(context, message);
    }

    private static void Write(string context, string message)
    {
        try
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {context} | {message}{Environment.NewLine}";

            lock (Gate)
            {
                RotateIfNeeded();
                File.AppendAllText(LogPath, line, Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never surface as a failure of the operation being logged.
        }
    }

    private static void RotateIfNeeded()
    {
        try
        {
            var info = new FileInfo(LogPath);
            if (!info.Exists || info.Length < MaxBytes) return;

            var backup = LogPath + ".1";
            if (File.Exists(backup)) File.Delete(backup);
            File.Move(LogPath, backup);
        }
        catch { }
    }
}
