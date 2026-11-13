using System.Net;
using System.Text;

namespace RunnerWatch.Tests;

/// <summary>
/// In-memory GitHub API: map a path prefix (without the query string) to a JSON body or a status code.
/// Records every request so tests can assert on headers (e.g. If-None-Match) and call counts.
/// </summary>
public sealed class FakeGitHub : HttpMessageHandler
{
    readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> _routes = new();
    public List<HttpRequestMessage> Requests { get; } = new();

    public FakeGitHub Json(string path, string json, string? etag = null) => Route(path, req =>
    {
        if (etag != null && req.Headers.TryGetValues("If-None-Match", out var v) && v.Contains(etag))
            return new HttpResponseMessage(HttpStatusCode.NotModified);
        var resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (etag != null) resp.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(etag);
        resp.Headers.Add("x-ratelimit-remaining", "4999");
        return resp;
    });

    public FakeGitHub Status(string path, HttpStatusCode code) => Route(path, _ => new HttpResponseMessage(code) { Content = new StringContent("{}") });

    public FakeGitHub Route(string path, Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        _routes[path] = handler;
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request);
        var path = request.RequestUri!.AbsolutePath.TrimStart('/');
        return Task.FromResult(_routes.TryGetValue(path, out var handler)
            ? handler(request)
            : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") });
    }

    public int Count(string pathPrefix) => Requests.Count(r => r.RequestUri!.AbsolutePath.TrimStart('/').StartsWith(pathPrefix, StringComparison.Ordinal));
}
