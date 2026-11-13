using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace RunnerWatch.Core.Platform;

/// <summary>
/// The app's own token, kept in the OS keychain (never in a file):
/// Windows Credential Manager, macOS Keychain (<c>security</c>), Linux Secret Service (<c>secret-tool</c>, libsecret).
/// On Linux without libsecret, saving is unavailable and the user should use <c>gh auth login</c> or GH_TOKEN.
/// </summary>
public static class CredentialStore
{
    const string Service = "RunnerWatch";
    const string Account = "github-token";

    public static bool CanSave =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || ProcessRunner.Exists("secret-tool");

    public static string? Read()
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return WinCred.Read("RunnerWatch:github.com") ?? WinCred.Read("GhActionsMonitor:github.com"); // legacy name
            if (OperatingSystem.IsMacOS())
                return ProcessRunner.Run("security", ["find-generic-password", "-s", Service, "-a", Account, "-w"])?.Trim();
            if (OperatingSystem.IsLinux())
                return ProcessRunner.Run("secret-tool", ["lookup", "service", Service, "account", Account])?.Trim();
        }
        catch (Exception ex) { Log.Warn("Could not read the saved token", ex); }
        return null;
    }

    /// <exception cref="InvalidOperationException">No keychain available on this system.</exception>
    public static void Write(string token)
    {
        if (OperatingSystem.IsWindows()) { WinCred.Write("RunnerWatch:github.com", token); return; }
        if (OperatingSystem.IsMacOS())
        {
            // The secret goes in argv for a fraction of a second: `security` has no stdin mode.
            // Acceptable on a single-user Mac; documented in SECURITY.md.
            Ok(ProcessRunner.Run("security", ["add-generic-password", "-U", "-s", Service, "-a", Account, "-w", token]) != null);
            return;
        }
        if (OperatingSystem.IsLinux() && ProcessRunner.Exists("secret-tool"))
        {
            // secret-tool reads the secret from stdin: it never shows up in the process list.
            Ok(ProcessRunner.Run("secret-tool", ["store", "--label=RunnerWatch GitHub token", "service", Service, "account", Account], stdin: token) != null);
            return;
        }
        throw new InvalidOperationException("No keychain available (install libsecret-tools, or use `gh auth login`).");

        static void Ok(bool ok) { if (!ok) throw new InvalidOperationException("The system keychain refused to save the token."); }
    }

    public static void Delete()
    {
        try
        {
            if (OperatingSystem.IsWindows()) { WinCred.Delete("RunnerWatch:github.com"); WinCred.Delete("GhActionsMonitor:github.com"); }
            else if (OperatingSystem.IsMacOS()) ProcessRunner.Run("security", ["delete-generic-password", "-s", Service, "-a", Account]);
            else if (OperatingSystem.IsLinux()) ProcessRunner.Run("secret-tool", ["clear", "service", Service, "account", Account]);
        }
        catch (Exception ex) { Log.Warn("Could not delete the saved token", ex); }
    }

    static class WinCred
    {
        const int CRED_TYPE_GENERIC = 1;
        const int CRED_PERSIST_LOCAL_MACHINE = 2;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct CREDENTIAL
        {
            public int Flags;
            public int Type;
            public string TargetName;
            public string? Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public int CredentialBlobSize;
            public IntPtr CredentialBlob;
            public int Persist;
            public int AttributeCount;
            public IntPtr Attributes;
            public string? TargetAlias;
            public string? UserName;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CredRead(string target, int type, int flags, out IntPtr credential);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CredWrite(ref CREDENTIAL credential, int flags);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CredDelete(string target, int type, int flags);
        [DllImport("advapi32.dll")]
        static extern void CredFree(IntPtr buffer);

        public static string? Read(string target)
        {
            if (!CredRead(target, CRED_TYPE_GENERIC, 0, out var ptr)) return null;
            try
            {
                var c = Marshal.PtrToStructure<CREDENTIAL>(ptr);
                return c.CredentialBlobSize == 0 ? null : Marshal.PtrToStringUni(c.CredentialBlob, c.CredentialBlobSize / 2);
            }
            finally { CredFree(ptr); }
        }

        public static void Write(string target, string token)
        {
            var bytes = Encoding.Unicode.GetBytes(token);
            var blob = Marshal.AllocHGlobal(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, blob, bytes.Length);
                var c = new CREDENTIAL
                {
                    Type = CRED_TYPE_GENERIC,
                    TargetName = target,
                    CredentialBlob = blob,
                    CredentialBlobSize = bytes.Length,
                    Persist = CRED_PERSIST_LOCAL_MACHINE,
                    UserName = "token",
                };
                if (!CredWrite(ref c, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally { Marshal.FreeHGlobal(blob); }
        }

        public static void Delete(string target) => CredDelete(target, CRED_TYPE_GENERIC, 0);
    }
}

/// <summary>Runs a CLI without a shell (arguments are never interpolated into a command line).</summary>
public static class ProcessRunner
{
    /// <returns>stdout on exit code 0, otherwise null (also when the program isn't installed or times out).</returns>
    public static string? Run(string file, IEnumerable<string> args, string? stdin = null, TimeSpan? timeout = null,
        IDictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin != null,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env != null) foreach (var (k, v) in env) psi.Environment[k] = v;
        try
        {
            using var p = Process.Start(psi);
            if (p == null) return null;
            if (stdin != null) { p.StandardInput.Write(stdin); p.StandardInput.Close(); }
            var outTask = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeout ?? TimeSpan.FromSeconds(10))) { try { p.Kill(true); } catch { } return null; }
            return p.ExitCode == 0 ? outTask.Result : null;
        }
        catch (Win32Exception) { return null; } // not installed
        catch (IOException) { return null; }
    }

    /// <summary>True if the program is on PATH.</summary>
    public static bool Exists(string file)
    {
        var exts = OperatingSystem.IsWindows() ? new[] { ".exe", ".cmd", ".bat", "" } : new[] { "" };
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(dir => exts.Any(e => File.Exists(Path.Combine(dir, file + e))));
    }
}
