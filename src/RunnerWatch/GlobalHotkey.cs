using System.Runtime.InteropServices;
using RunnerWatch.Core;

namespace RunnerWatch;

/// <summary>
/// Ctrl+Alt+G on Windows. Uses a dedicated thread with its own Win32 message queue
/// (RegisterHotKey with a null window posts WM_HOTKEY to that thread), so it doesn't depend on UI framework internals.
/// Other platforms have no portable global hotkey API: use the tray icon there.
/// </summary>
public sealed class GlobalHotkey : IDisposable
{
    const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_NOREPEAT = 0x4000, VK_G = 0x47;
    const uint WM_HOTKEY = 0x0312, WM_QUIT = 0x0012;
    const int HotkeyId = 0x5257;

    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

    readonly Thread? _thread;
    uint _threadId;

    public GlobalHotkey(Action onPressed)
    {
        if (!OperatingSystem.IsWindows()) return;
        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            if (!RegisterHotKey(IntPtr.Zero, HotkeyId, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_G))
            {
                Log.Info("Ctrl+Alt+G is taken by another app; hotkey not registered");
                return;
            }
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
                if (msg.message == WM_HOTKEY && msg.wParam == HotkeyId) onPressed();
            UnregisterHotKey(IntPtr.Zero, HotkeyId);
        })
        { IsBackground = true, Name = "GlobalHotkey" };
        _thread.Start();
    }

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
    }
}
