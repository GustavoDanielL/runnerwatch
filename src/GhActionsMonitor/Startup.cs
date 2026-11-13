using System;
using Microsoft.Win32;

namespace GhActionsMonitor;

/// <summary>Inicia com o Windows via HKCU\...\Run (não precisa de admin).</summary>
public static class WindowsStartup
{
    const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "GhActionsMonitor";

    public static void Apply(bool enabled)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(Key, writable: true) ?? Registry.CurrentUser.CreateSubKey(Key);
            if (enabled && Environment.ProcessPath is { } exe)
                k.SetValue(Name, $"\"{exe}\""); // sempre reaponta para o exe atual (se ele foi movido)
            else
                k.DeleteValue(Name, throwOnMissingValue: false);
        }
        catch { /* política de grupo pode bloquear; não é fatal */ }
    }
}
