using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace GhActionsMonitor;

/// <summary>
/// Log em arquivo: %LOCALAPPDATA%\GhActionsMonitor\logs\app.log (rotação em 1 MB, guarda 1 anterior).
/// Mensagens idênticas repetidas em até 60s viram uma linha "(repetiu N vezes)", para que um
/// erro em laço não encha o disco. Nunca lança exceção: logar não pode derrubar o app.
/// </summary>
public static class Log
{
    const long MaxBytes = 1024 * 1024;
    static readonly TimeSpan DedupeWindow = TimeSpan.FromSeconds(60);
    static readonly object Gate = new();
    static readonly Dictionary<string, (DateTimeOffset First, int Suppressed)> Recent = new();

    public static string Dir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GhActionsMonitor", "logs");
    public static string FilePath { get; } = Path.Combine(Dir, "app.log");

    public static void Info(string msg) => Write("INFO", msg, null);
    public static void Warn(string msg, Exception? ex = null) => Write("WARN", msg, ex);
    public static void Error(string msg, Exception? ex = null) => Write("ERRO", msg, ex);
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
        catch { /* sem log, mas sem derrubar o app */ }
    }

    /// <summary>Grava os contadores de repetição pendentes (chamar ao sair).</summary>
    public static void Flush()
    {
        try { lock (Gate) FlushExpired(DateTimeOffset.MaxValue); } catch { }
    }

    static void FlushExpired(DateTimeOffset now)
    {
        List<string>? done = null;
        foreach (var (k, v) in Recent)
        {
            if (now - v.First < DedupeWindow) continue;
            (done ??= new()).Add(k);
            if (v.Suppressed > 0)
            {
                var parts = k.Split('|');
                Append($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {parts[0].PadRight(5)} (repetiu mais {v.Suppressed}x) {parts[1]}");
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
