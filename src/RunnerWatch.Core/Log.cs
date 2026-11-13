using System.Text;

namespace RunnerWatch.Core;

/// <summary>
/// File log: <c>%LOCALAPPDATA%\RunnerWatch\logs\app.log</c> on Windows, <c>~/.local/share/RunnerWatch/logs/app.log</c>
/// on Linux/macOS. Rotates at 1 MB (keeps one previous file). Identical messages repeated within 60 s are
/// collapsed into a single "(repeated N more times)" line so an error loop can't fill the disk.
/// Never throws: logging must not bring the app down.
/// </summary>
public static class Log
{
    const long MaxBytes = 1024 * 1024;
    static readonly TimeSpan DedupeWindow = TimeSpan.FromSeconds(60);
    static readonly object Gate = new();
    static readonly Dictionary<string, (DateTimeOffset First, int Suppressed)> Recent = new();

    public static string Dir { get; private set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
        "RunnerWatch", "logs");

    public static string FilePath => Path.Combine(Dir, "app.log");

    /// <summary>Redirects the log (tests).</summary>
    internal static void UseDirectory(string dir)
    {
        lock (Gate) { Dir = dir; Recent.Clear(); }
    }

    public static void Info(string msg) => Write("INFO", msg, null);
    public static void Warn(string msg, Exception? ex = null) => Write("WARN", msg, ex);
    public static void Error(string msg, Exception? ex = null) => Write("ERROR", msg, ex);
    public static void Fatal(string msg, Exception? ex = null) => Write("FATAL", msg, ex);

    static void Write(string level, string msg, Exception? ex)
    {
        try
        {
            lock (Gate)
            {
                var now = DateTimeOffset.Now;
                var key = level + "|" + msg + "|" + ex?.GetType().FullName + "|" + ex?.Message;

                FlushExpired(now);
                if (Recent.TryGetValue(key, out var r))
                {
                    Recent[key] = (r.First, r.Suppressed + 1);
                    return;
                }
                Recent[key] = (now, 0);

                var sb = new StringBuilder();
                sb.Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(' ').Append(level.PadRight(5)).Append(' ').Append(msg);
                if (ex != null) sb.AppendLine().Append("    ").Append(ex.ToString().Replace("\n", "\n    "));
                Append(sb.ToString());
            }
        }
        catch { /* no log, but no crash either */ }
    }

    /// <summary>Writes pending repeat counters (call on exit).</summary>
    public static void Flush()
    {
        try { lock (Gate) FlushExpired(DateTimeOffset.MaxValue); } catch { }
    }

    static void FlushExpired(DateTimeOffset now)
    {
        List<string>? done = null;
        foreach (var (k, v) in Recent)
        {
            if (now != DateTimeOffset.MaxValue && now - v.First < DedupeWindow) continue;
            (done ??= new()).Add(k);
            if (v.Suppressed > 0)
            {
                var parts = k.Split('|');
                Append($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {parts[0].PadRight(5)} (repeated {v.Suppressed} more times) {parts[1]}");
            }
        }
        if (done != null) foreach (var k in done) Recent.Remove(k);
    }

    static void Append(string line)
    {
        Directory.CreateDirectory(Dir);
        var fi = new FileInfo(FilePath);
        if (fi.Exists && fi.Length > MaxBytes)
            File.Move(FilePath, FilePath + ".1", overwrite: true);
        File.AppendAllText(FilePath, line + Environment.NewLine, Encoding.UTF8);
    }
}
