#!/usr/bin/env python3
"""
Metrics agent for GitHub Actions self-hosted runners (part of RunnerWatch).

Serves GET /metrics -> {"cpu_percent", "mem_used", "mem_total", "hostname"}.
Standard library only. Works on Linux and Windows (macOS: total RAM only).

Usage:
    python3 runner_agent.py [--host 0.0.0.0] [--port 9465] [--token SEGREDO]
    (the token can also come from RUNNER_AGENT_TOKEN)

Only aggregate machine CPU/RAM is exposed. Still, on an untrusted network use --token,
or --host 127.0.0.1 behind a tunnel/VPN.
"""
import argparse
import hmac
import json
import os
import platform
import socket
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

SAMPLE_SECONDS = 2.0


class Sampler:
    """Amostra CPU em background para que /metrics responda instantaneamente."""

    def __init__(self):
        self.cpu = None
        self._prev = None
        self._lock = threading.Lock()
        self._read_cpu, self._read_mem = _platform_readers()
        t = threading.Thread(target=self._loop, daemon=True)
        t.start()

    def _loop(self):
        while True:
            try:
                cur = self._read_cpu()
                if cur and self._prev:
                    idle = cur[0] - self._prev[0]
                    total = cur[1] - self._prev[1]
                    if total > 0:
                        with self._lock:
                            self.cpu = round(100.0 * (total - idle) / total, 1)
                self._prev = cur
            except Exception:
                pass
            time.sleep(SAMPLE_SECONDS)

    def snapshot(self):
        used, total = (None, None)
        try:
            used, total = self._read_mem()
        except Exception:
            pass
        with self._lock:
            cpu = self.cpu
        return {"cpu_percent": cpu, "mem_used": used, "mem_total": total, "hostname": socket.gethostname()}


# ---------------- Linux ----------------
def _linux_cpu():
    with open("/proc/stat") as f:
        parts = [int(x) for x in f.readline().split()[1:]]
    idle = parts[3] + (parts[4] if len(parts) > 4 else 0)  # idle + iowait
    total = sum(parts[:8])  # skip guest (already counted in user)
    return idle, total


def _linux_mem():
    info = {}
    with open("/proc/meminfo") as f:
        for line in f:
            k, v = line.split(":", 1)
            info[k] = int(v.split()[0]) * 1024
    total = info["MemTotal"]
    avail = info.get("MemAvailable", info.get("MemFree", 0))
    return total - avail, total


# ---------------- Windows ----------------
def _windows_readers():
    import ctypes
    from ctypes import wintypes

    class FILETIME(ctypes.Structure):
        _fields_ = [("lo", wintypes.DWORD), ("hi", wintypes.DWORD)]

    class MEMORYSTATUSEX(ctypes.Structure):
        _fields_ = [("dwLength", wintypes.DWORD), ("dwMemoryLoad", wintypes.DWORD),
                    ("ullTotalPhys", ctypes.c_ulonglong), ("ullAvailPhys", ctypes.c_ulonglong),
                    ("ullTotalPageFile", ctypes.c_ulonglong), ("ullAvailPageFile", ctypes.c_ulonglong),
                    ("ullTotalVirtual", ctypes.c_ulonglong), ("ullAvailVirtual", ctypes.c_ulonglong),
                    ("ullAvailExtendedVirtual", ctypes.c_ulonglong)]

    k32 = ctypes.windll.kernel32

    def ft(x):
        return (x.hi << 32) | x.lo

    def cpu():
        i, k, u = FILETIME(), FILETIME(), FILETIME()
        if not k32.GetSystemTimes(ctypes.byref(i), ctypes.byref(k), ctypes.byref(u)):
            return None
        return ft(i), ft(k) + ft(u)  # kernel time already includes idle

    def mem():
        m = MEMORYSTATUSEX()
        m.dwLength = ctypes.sizeof(MEMORYSTATUSEX)
        k32.GlobalMemoryStatusEx(ctypes.byref(m))
        return m.ullTotalPhys - m.ullAvailPhys, m.ullTotalPhys

    return cpu, mem


def _platform_readers():
    system = platform.system()
    if system == "Linux":
        return _linux_cpu, _linux_mem
    if system == "Windows":
        return _windows_readers()

    def none_cpu():
        return None

    def mac_mem():
        import subprocess
        total = int(subprocess.check_output(["sysctl", "-n", "hw.memsize"]).strip())
        return None, total

    return none_cpu, mac_mem


def make_handler(sampler, token):
    class Handler(BaseHTTPRequestHandler):
        server_version = "runner-agent/0.1"

        def do_GET(self):
            if self.path.split("?")[0] != "/metrics":
                self.send_error(404)
                return
            if token and not hmac.compare_digest(self.headers.get("X-Agent-Token", ""), token):
                self.send_error(401)
                return
            body = json.dumps(sampler.snapshot()).encode()
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.send_header("Cache-Control", "no-store")
            self.end_headers()
            self.wfile.write(body)

        def log_message(self, *args):  # silencioso
            pass

    return Handler


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--host", default="0.0.0.0")
    ap.add_argument("--port", type=int, default=9465)
    ap.add_argument("--token", default=os.environ.get("RUNNER_AGENT_TOKEN"))
    args = ap.parse_args()

    sampler = Sampler()
    server = ThreadingHTTPServer((args.host, args.port), make_handler(sampler, args.token))
    print(f"runner-agent em http://{args.host}:{args.port}/metrics"
          f"{' (com token)' if args.token else ''}", file=sys.stderr, flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
