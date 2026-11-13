using RunnerWatch.Core.Platform;

namespace RunnerWatch.Core;

/// <summary>
/// Finds a GitHub token by reusing what the machine already has, in this order:
///   1. GH_TOKEN / GITHUB_TOKEN environment variable
///   2. token saved by the app in the OS keychain
///   3. <c>gh auth token</c> (GitHub CLI)
///   4. <c>git credential fill</c> (Git Credential Manager, bundled with Git for Windows)
/// Each candidate is validated against /user; the first one that works wins.
/// SSH keys do NOT work: the REST API doesn't accept SSH.
/// </summary>
public static class TokenProvider
{
    public sealed record Auth(string Token, string Source, string Login);

    /// <summary>Thrown when validation fails for lack of network (e.g. right after boot).</summary>
    public sealed class NetworkUnavailableException(Exception inner) : Exception("Network unavailable", inner);

    /// <summary>
    /// Returns null only when NO credential exists or works. If the network is down, throws
    /// <see cref="NetworkUnavailableException"/> so the caller retries instead of asking for a token.
    /// </summary>
    public static async Task<Auth?> ResolveAsync(string apiBaseUrl, Action<string>? progress, CancellationToken ct)
    {
        var host = GitHost(apiBaseUrl);
        foreach (var (source, getter) in Candidates(host))
        {
            progress?.Invoke(L.F("status.lookingFor", source));
            string? token;
            try { token = await Task.Run(getter, ct).ConfigureAwait(false); }
            catch (Exception ex) when (!ct.IsCancellationRequested) { Log.Warn($"Credential source failed: {source}", ex); token = null; }
            if (string.IsNullOrWhiteSpace(token)) continue;
            token = token.Trim();

            using var api = new GitHubApi(token, apiBaseUrl);
            try
            {
                var user = await api.GetAsync("user", GitHubJson.Default.GhUser, ct).ConfigureAwait(false);
                if (user?.Login is { Length: > 0 } login) return new Auth(token, source, login);
            }
            catch (GitHubException ex) { Log.Info($"Credential from {source} rejected: {(int)ex.Status}"); /* 401, or SSO/IP allowlist 403 */ }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new NetworkUnavailableException(ex);
            }
        }
        return null;
    }

    /// <summary>api.github.com → github.com; https://ghe.example/api/v3 → ghe.example</summary>
    internal static string GitHost(string apiBaseUrl)
    {
        var h = new Uri(apiBaseUrl).Host;
        return h.Equals("api.github.com", StringComparison.OrdinalIgnoreCase) ? "github.com" : h;
    }

    static IEnumerable<(string, Func<string?>)> Candidates(string host)
    {
        yield return (L.T("source.env"),
            () => Environment.GetEnvironmentVariable("GH_TOKEN") ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN"));
        yield return (L.T("source.saved"), CredentialStore.Read);
        yield return (L.T("source.gh"), () => ProcessRunner.Run("gh", ["auth", "token", "--hostname", host]));
        yield return (L.T("source.gcm"), () => GitCredentialFill(host));
    }

    static string? GitCredentialFill(string host)
    {
        // May open the GCM sign-in window if there is no credential yet: that's intended.
        var output = ProcessRunner.Run("git", ["credential", "fill"], $"protocol=https\nhost={host}\n\n", TimeSpan.FromMinutes(2),
            new Dictionary<string, string> { ["GIT_TERMINAL_PROMPT"] = "0", ["GCM_INTERACTIVE"] = "auto" });
        if (output == null) return null;
        foreach (var line in output.Split('\n'))
            if (line.StartsWith("password=", StringComparison.Ordinal))
                return line["password=".Length..].Trim();
        return null;
    }
}
