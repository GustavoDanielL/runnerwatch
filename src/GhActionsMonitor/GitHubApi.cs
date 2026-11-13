using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GhActionsMonitor;

public class GitHubException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

public sealed class RateLimitException(DateTimeOffset reset)
    : GitHubException(HttpStatusCode.Forbidden, "Rate limit da API do GitHub atingido")
{
    public DateTimeOffset Reset { get; } = reset;
}

/// <summary>
/// Cliente mínimo da API REST com cache por ETag: respostas 304 não contam no rate limit
/// e devolvem o objeto já desserializado da última vez.
/// </summary>
public sealed class GitHubApi : IDisposable
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    sealed class CacheEntry(string etag, object? value)
    {
        public string ETag { get; } = etag;
        public object? Value { get; } = value;
        public DateTimeOffset LastUsed { get; set; } = DateTimeOffset.UtcNow;
    }

    readonly HttpClient _http;
    readonly ConcurrentDictionary<string, CacheEntry> _cache = new();

    public int RateRemaining { get; private set; } = -1;
    public DateTimeOffset RateReset { get; private set; }

    public GitHubApi(string token, string baseUrl = "https://api.github.com/")
    {
        _http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        {
            BaseAddress = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/"),
            Timeout = TimeSpan.FromSeconds(20),
        };
        if (!string.IsNullOrEmpty(token))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("gh-actions-monitor/0.1");
    }

    public async Task<T?> GetAsync<T>(string path, CancellationToken ct = default) where T : class
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        _cache.TryGetValue(path, out var cached);
        if (cached != null) req.Headers.TryAddWithoutValidation("If-None-Match", cached.ETag);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        ReadRate(resp);

        if (resp.StatusCode == HttpStatusCode.NotModified && cached != null)
        {
            cached.LastUsed = DateTimeOffset.UtcNow;
            return cached.Value as T;
        }

        if (!resp.IsSuccessStatusCode)
        {
            if ((resp.StatusCode == HttpStatusCode.Forbidden && RateRemaining == 0) || (int)resp.StatusCode == 429)
            {
                var reset = RateReset;
                if (resp.Headers.RetryAfter?.Delta is { } d) reset = DateTimeOffset.UtcNow + d;
                if (reset <= DateTimeOffset.UtcNow) reset = DateTimeOffset.UtcNow.AddMinutes(1);
                throw new RateLimitException(reset);
            }
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new GitHubException(resp.StatusCode, $"{(int)resp.StatusCode} em {path}: {Trim(body)}");
        }

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var value = JsonSerializer.Deserialize<T>(json, Json);
        var etag = resp.Headers.ETag?.ToString();
        if (!string.IsNullOrEmpty(etag)) _cache[path] = new CacheEntry(etag, value);
        return value;
    }

    /// <summary>Remove entradas do cache que não foram usadas há um tempo (runs antigos etc.).</summary>
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
