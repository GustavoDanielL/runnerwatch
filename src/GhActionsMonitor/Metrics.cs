using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GhActionsMonitor;

/// <summary>Busca CPU/RAM dos runners self-hosted (agente HTTP ou a própria máquina).</summary>
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

        // Runner nesta mesma máquina e sem agente configurado: lê direto, sem precisar de agente.
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
            var full = url.TrimEnd('/') + "/metrics";
            using var resp = await _http.GetAsync(full, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<AgentMetrics>(json, GitHubApi.Json);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Agente inexistente/offline: não tenta de novo por 1 min para não travar o ciclo.
            _backoff[runnerName] = DateTimeOffset.UtcNow.AddMinutes(1);
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>CPU/RAM da máquina local via Win32 (sem PerformanceCounter, que é pesado).</summary>
public sealed class LocalMetrics
{
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

    long _idle, _kernel, _user;
    bool _hasPrev;

    public AgentMetrics? Sample()
    {
        double? cpu = null;
        if (GetSystemTimes(out var idle, out var kernel, out var user))
        {
            if (_hasPrev)
            {
                long dIdle = idle - _idle, total = (kernel - _kernel) + (user - _user); // kernel inclui idle
                if (total > 0) cpu = Math.Round(100.0 * (total - dIdle) / total, 1);
            }
            (_idle, _kernel, _user, _hasPrev) = (idle, kernel, user, true);
        }

        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref mem)) return new AgentMetrics(cpu, null, null, Environment.MachineName);
        return new AgentMetrics(cpu, (long)(mem.ullTotalPhys - mem.ullAvailPhys), (long)mem.ullTotalPhys, Environment.MachineName);
    }
}
