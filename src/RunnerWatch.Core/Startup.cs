using System.Security;
using Microsoft.Win32;

namespace RunnerWatch.Core;

/// <summary>
/// Start with the system, per user (no admin):
/// Windows <c>HKCU\...\Run</c> · Linux <c>~/.config/autostart/runnerwatch.desktop</c> · macOS <c>~/Library/LaunchAgents</c>.
/// </summary>
public static class SystemStartup
{
    const string Name = "RunnerWatch";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string MacLabel = "io.github.gustavodaniell.runnerwatch";

    public static void Apply(bool enabled)
    {
        var exe = AppExecutable();
        if (exe == null) return;
        try
        {
            if (OperatingSystem.IsWindows()) Windows(enabled, exe);
            else if (OperatingSystem.IsLinux()) Linux(enabled, exe);
            else if (OperatingSystem.IsMacOS()) MacOS(enabled, exe);
        }
        catch (Exception ex) { Log.Warn("Could not update start-with-system", ex); } // e.g. blocked by policy
    }

    /// <summary>The binary to launch. Inside a macOS .app bundle this is still the inner executable.</summary>
    static string? AppExecutable() => Environment.ProcessPath;

    static void Windows(bool enabled, string exe)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
        k.DeleteValue("GhActionsMonitor", throwOnMissingValue: false); // previous name
        if (enabled) k.SetValue(Name, $"\"{exe}\""); // always re-points to the current exe (if it was moved)
        else k.DeleteValue(Name, throwOnMissingValue: false);
    }

    static void Linux(bool enabled, string exe)
    {
        var dir = Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
                               ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"), "autostart");
        var file = Path.Combine(dir, "runnerwatch.desktop");
        if (!enabled) { File.Delete(file); return; }
        Directory.CreateDirectory(dir);
        File.WriteAllText(file, $"""
            [Desktop Entry]
            Type=Application
            Name=RunnerWatch
            Comment=GitHub Actions monitor
            Exec="{exe}"
            Icon=runnerwatch
            X-GNOME-Autostart-enabled=true
            NoDisplay=true

            """);
    }

    static void MacOS(bool enabled, string exe)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents");
        var file = Path.Combine(dir, MacLabel + ".plist");
        if (!enabled) { File.Delete(file); return; }
        Directory.CreateDirectory(dir);
        File.WriteAllText(file, $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>Label</key><string>{MacLabel}</string>
              <key>ProgramArguments</key><array><string>{SecurityElement.Escape(exe)}</string></array>
              <key>RunAtLoad</key><true/>
              <key>ProcessType</key><string>Interactive</string>
            </dict>
            </plist>

            """);
    }
}
