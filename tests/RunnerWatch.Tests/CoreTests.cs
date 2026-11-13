using System.Globalization;
using System.Net;
using System.Text.Json;
using RunnerWatch.Core;

namespace RunnerWatch.Tests;

public class GitHubApiTests
{
    [Fact]
    public async Task Second_request_sends_If_None_Match_and_reuses_the_cached_object_on_304()
    {
        var gh = new FakeGitHub().Json("user", """{"login":"octocat"}""", etag: "\"v1\"");
        using var api = new GitHubApi("t", "https://api.github.com/", gh);
        var ct = TestContext.Current.CancellationToken;

        var first = await api.GetAsync("user", GitHubJson.Default.GhUser, ct);
        var second = await api.GetAsync("user", GitHubJson.Default.GhUser, ct);

        Assert.Equal("octocat", second?.Login);
        Assert.Same(first, second);
        Assert.True(gh.Requests[1].Headers.Contains("If-None-Match"));
    }

    [Fact]
    public async Task Rate_limit_403_becomes_RateLimitException()
    {
        var gh = new FakeGitHub().Route("user", _ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{}") };
            r.Headers.Add("x-ratelimit-remaining", "0");
            r.Headers.Add("x-ratelimit-reset", DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
            return r;
        });
        using var api = new GitHubApi("t", "https://api.github.com/", gh);

        await Assert.ThrowsAsync<RateLimitException>(() => api.GetAsync("user", GitHubJson.Default.GhUser, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Sends_a_descriptive_user_agent() => Assert.StartsWith("RunnerWatch/", GitHubApi.UserAgent);
}


public class LogTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "rw-log-" + Guid.NewGuid().ToString("N"));

    public LogTests() => Log.UseDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Repeated_errors_are_collapsed_instead_of_flooding_the_file()
    {
        for (int i = 0; i < 500; i++) Log.Error("same failure", new InvalidOperationException("boom"));
        Log.Flush();

        var lines = File.ReadAllLines(Log.FilePath).Where(l => !l.StartsWith("    ", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, lines.Count);
        Assert.Contains("repeated 499 more times", lines[1]);
    }

    [Fact]
    public void Exceptions_are_written_with_their_stack()
    {
        Log.Error("with exception", new InvalidOperationException("details here"));
        Assert.Contains("details here", File.ReadAllText(Log.FilePath));
    }
}

public class ConfigTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("rw-cfg-").FullName;
    string PathOf(string name) => Path.Combine(_dir, name);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Missing_file_creates_defaults()
    {
        var cfg = AppConfig.Load(PathOf("config.json"));
        Assert.Equal(10, cfg.AutoRepoCount);
        Assert.True(File.Exists(PathOf("config.json")));
    }

    [Fact]
    public void Comments_and_trailing_commas_are_accepted()
    {
        File.WriteAllText(PathOf("config.json"), """
            // my repos
            { "repos": ["acme/api",], "idlePollSeconds": 120, }
            """);
        var cfg = AppConfig.Load(PathOf("config.json"));
        Assert.Equal(["acme/api"], cfg.Repos);
        Assert.Equal(120, cfg.IdlePollSeconds);
    }

    [Fact]
    public void Out_of_range_values_are_clamped()
    {
        File.WriteAllText(PathOf("config.json"), """{ "activePollSeconds": 1, "idlePollSeconds": 2, "autoRepoCount": 999 }""");
        var cfg = AppConfig.Load(PathOf("config.json"));
        Assert.Equal(5, cfg.ActivePollSeconds);           // never hammer the API
        Assert.True(cfg.IdlePollSeconds >= cfg.ActivePollSeconds);
        Assert.Equal(50, cfg.AutoRepoCount);
    }

    [Fact]
    public void Broken_file_is_kept_aside_and_defaults_are_used()
    {
        File.WriteAllText(PathOf("config.json"), "{ not json");
        var cfg = AppConfig.Load(PathOf("config.json"));
        Assert.Equal(10, cfg.AutoRepoCount);
        Assert.True(File.Exists(PathOf("config.json.broken")));
    }
}

public class TranslationTests
{
    [Fact]
    public void Every_language_has_exactly_the_english_keys()
    {
        var en = L.Load("en")!;
        foreach (var code in L.Available.Where(c => c != "en"))
        {
            var other = L.Load(code)!;
            Assert.True(en.Keys.Except(other.Keys).ToList() is { Count: 0 }, $"{code} is missing: {string.Join(", ", en.Keys.Except(other.Keys))}");
            Assert.True(other.Keys.Except(en.Keys).ToList() is { Count: 0 }, $"{code} has unknown keys: {string.Join(", ", other.Keys.Except(en.Keys))}");
        }
    }

    [Fact]
    public void Placeholders_match_between_languages()
    {
        var en = L.Load("en")!;
        foreach (var code in L.Available.Where(c => c != "en"))
            foreach (var (key, value) in L.Load(code)!)
                Assert.True(Placeholders(en[key]).SetEquals(Placeholders(value)), $"{code}:{key} placeholders differ from English");

        static HashSet<string> Placeholders(string s) =>
            System.Text.RegularExpressions.Regex.Matches(s, @"\{\d+\}").Select(m => m.Value).ToHashSet();
    }

    [Theory]
    [InlineData("pt-BR", null, "pt-BR")]
    [InlineData("pt-PT", null, "pt-BR")]   // same language, other region
    [InlineData("de-DE", null, "en")]      // not translated yet: English
    [InlineData("pt-BR", "en", "en")]      // config wins over the OS
    [InlineData("en-US", "auto", "en")]
    public void Language_follows_the_OS_unless_configured(string os, string? configured, string expected) =>
        Assert.Equal(expected, L.Resolve(configured, CultureInfo.GetCultureInfo(os)));
}

public class DemoTests
{
    [Fact]
    public void Demo_shows_every_state_without_network()
    {
        var snap = new DemoMonitor().Build();

        Assert.Contains(snap.Runs, r => r.Stuck);
        Assert.Contains(snap.Runs, r => r.RunnerBusy);
        Assert.Contains(snap.Runs, r => r.IsDeploy && r.Phase == RunPhase.Running);
        Assert.Contains(snap.Runs, r => r.Phase == RunPhase.Failed);
        Assert.All(snap.Runs, r => Assert.StartsWith("acme/", r.Repo)); // never real data
    }

    [Fact]
    public async Task Demo_rerun_removes_the_failed_run()
    {
        var demo = new DemoMonitor();
        var failed = demo.Build().Runs.First(r => r.Phase == RunPhase.Failed);

        Assert.Null(await demo.RerunFailedAsync(failed, TestContext.Current.CancellationToken));
        Assert.DoesNotContain(demo.Build().Runs, r => r.Phase == RunPhase.Failed);
    }
}
