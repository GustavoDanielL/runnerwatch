using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using RunnerWatch.Core.Platform;

namespace RunnerWatch.Core;

/// <summary>Fetches CPU/RAM of self-hosted runners (HTTP agent, or this machine directly).</summary>
public sealed class MetricsCollector : IDisposable
{
    readonly AppConfig _cfg;
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };
    readonly Dictionary<string, DateTimeOffset> _backoff = new(StringComparer.OrdinalIgnoreCase);
    readonly LocalMetrics _local = new();

    public MetricsCollector(AppConfig cfg)
    {
        _cfg = cfg;
        if (!string.IsNullOrEmpty(cfg.AgentToken))
            _http.DefaultRequestHeaders.Add("X-Agent-Token", cfg.AgentToken);
    }

    public async Task<AgentMetrics?> GetAsync(string runnerName, CancellationToken ct)
    {
        string? url = _cfg.Agents.TryGetValue(runnerName, out var explicitUrl) ? explicitUrl : null;

        // Runner on this same machine and no agent configured: read it directly, no agent needed.
        if (url == null && string.Equals(runnerName, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            return _local.Sample();

        if (url == null)
        {
            if (string.IsNullOrWhiteSpace(_cfg.AgentUrlTemplate)) return null;
            url = _cfg.AgentUrlTemplate.Replace("{runner}", Uri.EscapeDataString(runnerName));
        }

        if (_backoff.TryGetValue(runnerName, out var until) && until > DateTimeOffset.UtcNow) return null;
        try
        {
            using var resp = await _http.GetAsync(url.TrimEnd('/') + "/metrics", ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize(json, GitHubJson.Default.AgentMetrics);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Agent missing/offline: don't retry for a minute so the cycle never stalls on it.
            _backoff[runnerName] = DateTimeOffset.UtcNow.AddMinutes(1);
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>CPU/RAM of this machine: Win32 on Windows, /proc on Linux, <c>top</c> on macOS.</summary>
public sealed class LocalMetrics
{
    long _idle, _total;
    bool _hasPrev;

    public AgentMetrics? Sample()
    {
        try
        {
            if (OperatingSystem.IsWindows()) return Windows();
            if (OperatingSystem.IsLinux()) return Linux();
            if (OperatingSystem.IsMacOS()) return MacOS();
        }
        catch (Exception ex) { Log.Warn("Local metrics failed", ex); }
        return null;
    }

    double? Cpu(long idle, long total)
    {
        double? cpu = null;
        if (_hasPrev)
        {
            long dIdle = idle - _idle, dTotal = total - _total;
            if (dTotal > 0) cpu = Math.Round(100.0 * (dTotal - dIdle) / dTotal, 1);
        }
        (_idle, _total, _hasPrev) = (idle, total, true);
        return cpu;
    }

    // ---------- Windows ----------
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile,
            ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

    AgentMetrics Windows()
    {
        double? cpu = GetSystemTimes(out var idle, out var kernel, out var user) ? Cpu(idle, kernel + user) : null; // kernel includes idle
        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref mem)
            ? new AgentMetrics(cpu, (long)(mem.ullTotalPhys - mem.ullAvailPhys), (long)mem.ullTotalPhys, Environment.MachineName)
            : new AgentMetrics(cpu, null, null, Environment.MachineName);
    }

    // ---------- Linux ----------
    AgentMetrics Linux()
    {
        var first = File.ReadLines("/proc/stat").First().Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1)
            .Select(x => long.Parse(x, CultureInfo.InvariantCulture)).ToArray();
        var cpu = Cpu(first[3] + (first.Length > 4 ? first[4] : 0), first.Take(8).Sum()); // idle+iowait / all but guest
        long? total = null, avail = null;
        foreach (var line in File.ReadLines("/proc/meminfo"))
        {
            if (line.StartsWith("MemTotal:", StringComparison.Ordinal)) total = Kb(line);
            else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal)) avail = Kb(line);
        }
        return new AgentMetrics(cpu, total - avail, total, Environment.MachineName);

        static long Kb(string line) => long.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], CultureInfo.InvariantCulture) * 1024;
    }

    // ---------- macOS ----------
    static AgentMetrics? MacOS()
    {
        // "CPU usage: 7.45% user, 9.31% sys, 83.23% idle" and "PhysMem: 15G used (2G wired), 512M unused."
        var top = ProcessRunner.Run("top", ["-l", "1", "-n", "0", "-s", "0"], timeout: TimeSpan.FromSeconds(5));
        if (top == null) return null;
        double? cpu = null;
        long? used = null, unused = null;
        foreach (var line in top.Split('\n'))
        {
            if (line.StartsWith("CPU usage:", StringComparison.Ordinal))
            {
                var idle = line.Split(',').FirstOrDefault(p => p.Contains("idle", StringComparison.Ordinal));
                if (idle != null && double.TryParse(idle.Trim().Split('%')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var i))
                    cpu = Math.Round(100 - i, 1);
            }
            else if (line.StartsWith("PhysMem:", StringComparison.Ordinal))
            {
                var parts = line["PhysMem:".Length..].Split(',');
                used = Size(parts[0].Trim().Split(' ')[0]);
                unused = parts.Length > 1 ? Size(parts[^1].Trim().Split(' ')[0]) : null;
            }
        }
        return new AgentMetrics(cpu, used, used + unused, Environment.MachineName);

        static long? Size(string s)
        {
            if (s.Length < 2 || !double.TryParse(s[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return null;
            return s[^1] switch { 'K' => (long)(v * 1024), 'M' => (long)(v * 1048576), 'G' => (long)(v * 1073741824), 'T' => (long)(v * 1099511627776), _ => null };
        }
    }
}
