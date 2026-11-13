using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GhActionsMonitor;

/// <summary>
/// Descobre um token do GitHub reaproveitando o que a máquina já tem, nesta ordem:
///   1. variável de ambiente GH_TOKEN / GITHUB_TOKEN
///   2. token salvo pelo próprio app no Cofre de Credenciais do Windows
///   3. `gh auth token` (GitHub CLI)
///   4. `git credential fill` (Git Credential Manager, que vem com o Git para Windows)
/// Cada candidato é validado contra /user; o primeiro que funcionar ganha.
/// Chave SSH NÃO serve: a API REST não aceita SSH.
/// </summary>
public static class TokenProvider
{
    public sealed record Auth(string Token, string Source, string Login);

    /// <summary>Lançada quando não dá para validar por falta de rede (ex.: logo após o boot).</summary>
    public sealed class NetworkUnavailableException(Exception inner) : Exception("Sem rede", inner);

    /// <summary>
    /// Retorna null só quando NENHUMA credencial existe/funciona. Se a rede estiver fora,
    /// lança NetworkUnavailableException para o chamador tentar de novo (não pede token à toa).
    /// </summary>
    public static async Task<Auth?> ResolveAsync(string apiBaseUrl, Action<string>? progress, CancellationToken ct)
    {
        var host = GitHost(apiBaseUrl);
        foreach (var (source, getter) in Candidates(host))
        {
            progress?.Invoke($"Procurando credencial: {source}…");
            string? token;
            try { token = await Task.Run(getter, ct).ConfigureAwait(false); }
            catch (Exception) when (!ct.IsCancellationRequested) { token = null; }
            if (string.IsNullOrWhiteSpace(token)) continue;
            token = token.Trim();

            using var api = new GitHubApi(token, apiBaseUrl);
            try
            {
                var user = await api.GetAsync<GhUser>("user", ct).ConfigureAwait(false);
                if (user?.Login is { Length: > 0 } login) return new Auth(token, source, login);
            }
            catch (GitHubException) { /* 401, ou 403 de SSO/IP allowlist: tenta o próximo */ }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new NetworkUnavailableException(ex);
            }
        }
        return null;
    }

    /// <summary>api.github.com → github.com; https://ghe.empresa/api/v3 → ghe.empresa</summary>
    static string GitHost(string apiBaseUrl)
    {
        var h = new Uri(apiBaseUrl).Host;
        return h.Equals("api.github.com", StringComparison.OrdinalIgnoreCase) ? "github.com" : h;
    }

    static IEnumerable<(string, Func<string?>)> Candidates(string host)
    {
        yield return ("variável GH_TOKEN/GITHUB_TOKEN",
            () => Environment.GetEnvironmentVariable("GH_TOKEN") ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN"));
        yield return ("token salvo no app", CredStore.Read);
        yield return ("GitHub CLI (gh)", () => Run("gh", $"auth token --hostname {host}", null, TimeSpan.FromSeconds(10)));
        yield return ("Git Credential Manager", () => GitCredentialFill(host));
    }

    static string? GitCredentialFill(string host)
    {
        // Pode abrir a janela de login do GCM se ainda não houver credencial: é o comportamento desejado.
        var output = Run("git", "credential fill", $"protocol=https\nhost={host}\n\n", TimeSpan.FromMinutes(2));
        if (output == null) return null;
        foreach (var line in output.Split('\n'))
            if (line.StartsWith("password=", StringComparison.Ordinal))
                return line["password=".Length..].Trim();
        return null;
    }

    static string? Run(string file, string args, string? stdin, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin != null,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GCM_INTERACTIVE"] = "auto";
        try
        {
            using var p = Process.Start(psi);
            if (p == null) return null;
            if (stdin != null) { p.StandardInput.Write(stdin); p.StandardInput.Close(); }
            var outTask = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeout)) { try { p.Kill(true); } catch { } return null; }
            return p.ExitCode == 0 ? outTask.Result : null;
        }
        catch (Win32Exception) { return null; } // programa não instalado
        catch (IOException) { return null; }
    }
}

/// <summary>Token próprio do app no Cofre de Credenciais do Windows (nunca em arquivo).</summary>
public static class CredStore
{
    const string Target = "GhActionsMonitor:github.com";
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

    public static string? Read()
    {
        if (!CredRead(Target, CRED_TYPE_GENERIC, 0, out var ptr)) return null;
        try
        {
            var c = Marshal.PtrToStructure<CREDENTIAL>(ptr);
            return c.CredentialBlobSize == 0 ? null : Marshal.PtrToStringUni(c.CredentialBlob, c.CredentialBlobSize / 2);
        }
        finally { CredFree(ptr); }
    }

    public static void Write(string token)
    {
        var bytes = Encoding.Unicode.GetBytes(token);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var c = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = Target,
                CredentialBlob = blob,
                CredentialBlobSize = bytes.Length,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
                UserName = "token",
            };
            if (!CredWrite(ref c, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            Marshal.FreeHGlobal(blob);
        }
    }

    public static void Delete() => CredDelete(Target, CRED_TYPE_GENERIC, 0);
}
