using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace RunnerWatch.Core;

public class GitHubException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

public sealed class RateLimitException(DateTimeOffset reset)
    : GitHubException(HttpStatusCode.Forbidden, "GitHub API rate limit reached")
{
    public DateTimeOffset Reset { get; } = reset;
}

/// <summary>
/// Minimal GitHub REST client with an ETag cache: 304 responses don't count against the rate limit
/// and return the object deserialized last time. Octokit is not used on purpose: it doesn't do
/// conditional requests out of the box and would add weight to the Native AOT binary.
/// </summary>
public sealed class GitHubApi : IDisposable
{
    sealed class CacheEntry(string etag, object? value)
    {
        public string ETag { get; } = etag;
        public object? Value { get; } = value;
        public DateTimeOffset LastUsed { get; set; } = DateTimeOffset.UtcNow;
    }

    public const string UserAgent = "RunnerWatch/0.2 (+https://github.com/GustavoDanielL/runnerwatch)";

    readonly HttpClient _http;
    readonly ConcurrentDictionary<string, CacheEntry> _cache = new();

    public int RateRemaining { get; private set; } = -1;
    public DateTimeOffset RateReset { get; private set; }

    public GitHubApi(string token, string baseUrl = "https://api.github.com/", HttpMessageHandler? handler = null)
    {
        _http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            // Redirects are followed by SendAsync below: the built-in handler drops the Authorization
            // header on redirect, so a renamed repo (301) would come back as 401 and look like a revoked token.
            AllowAutoRedirect = false,
        })
        {
            BaseAddress = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/"),
            Timeout = TimeSpan.FromSeconds(20),
        };
        if (!string.IsNullOrEmpty(token))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
    }

    public async Task<T?> GetAsync<T>(string path, JsonTypeInfo<T> type, CancellationToken ct = default) where T : class
    {
        _cache.TryGetValue(path, out var cached);
        using var resp = await SendAsync(HttpMethod.Get, path, cached?.ETag, ct).ConfigureAwait(false);

        if (resp.StatusCode == HttpStatusCode.NotModified && cached != null)
        {
            cached.LastUsed = DateTimeOffset.UtcNow;
            return cached.Value as T;
        }

        await ThrowIfFailedAsync(resp, path, ct).ConfigureAwait(false);

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var value = JsonSerializer.Deserialize(json, type);
        var etag = resp.Headers.ETag?.ToString();
        if (!string.IsNullOrEmpty(etag)) _cache[path] = new CacheEntry(etag, value);
        return value;
    }

    /// <summary>POST without a body (e.g. re-run endpoints).</summary>
    public async Task PostAsync(string path, CancellationToken ct = default)
    {
        using var resp = await SendAsync(HttpMethod.Post, path, null, ct).ConfigureAwait(false);
        await ThrowIfFailedAsync(resp, path, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a request and follows up to 3 redirects (renamed/transferred repos) keeping the token, but only
    /// to the same host as the API: the token is never sent to another origin.
    /// </summary>
    async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? etag, CancellationToken ct)
    {
        var uri = new Uri(_http.BaseAddress!, path);
        for (int hop = 0; ; hop++)
        {
            using var req = new HttpRequestMessage(method, uri);
            if (etag != null) req.Headers.TryAddWithoutValidation("If-None-Match", etag);
            var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            ReadRate(resp);

            var code = (int)resp.StatusCode;
            if (code is not (301 or 302 or 307 or 308) || resp.Headers.Location is not { } location || hop >= 3)
                return resp;

            var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
            if (!string.Equals(next.Host, _http.BaseAddress!.Host, StringComparison.OrdinalIgnoreCase) || next.Scheme != Uri.UriSchemeHttps)
                return resp; // never forward the token to another origin
            resp.Dispose();
            uri = next;
            if (code is 301 or 302 && method == HttpMethod.Post) method = HttpMethod.Get; // RFC 9110 semantics
        }
    }

    async Task ThrowIfFailedAsync(HttpResponseMessage resp, string path, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        if ((resp.StatusCode == HttpStatusCode.Forbidden && RateRemaining == 0) || (int)resp.StatusCode == 429)
        {
            var reset = RateReset;
            if (resp.Headers.RetryAfter?.Delta is { } d) reset = DateTimeOffset.UtcNow + d;
            if (reset <= DateTimeOffset.UtcNow) reset = DateTimeOffset.UtcNow.AddMinutes(1);
            throw new RateLimitException(reset);
        }
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        throw new GitHubException(resp.StatusCode, $"{(int)resp.StatusCode} on {path}: {Trim(body)}");
    }

    /// <summary>Drops cache entries not used for a while (old runs, etc.).</summary>
    public void Prune(TimeSpan olderThan)
    {
        var limit = DateTimeOffset.UtcNow - olderThan;
        foreach (var kv in _cache.Where(kv => kv.Value.LastUsed < limit).ToList())
            _cache.TryRemove(kv.Key, out _);
    }

    void ReadRate(HttpResponseMessage resp)
    {
        if (resp.Headers.TryGetValues("x-ratelimit-remaining", out var rem) && int.TryParse(rem.FirstOrDefault(), out var r))
            RateRemaining = r;
        if (resp.Headers.TryGetValues("x-ratelimit-reset", out var rs) && long.TryParse(rs.FirstOrDefault(), out var epoch))
            RateReset = DateTimeOffset.FromUnixTimeSeconds(epoch);
    }

    static string Trim(string s) => s.Length > 200 ? s[..200] + "…" : s;

    public void Dispose() => _http.Dispose();
}
