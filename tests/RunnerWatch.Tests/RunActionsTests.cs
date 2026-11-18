using System.Net;
using System.Text;
using RunnerWatch.Core;

namespace RunnerWatch.Tests;

/// <summary>Cancel / approve from the widget (#8) and hiding repos and workflows (#6).</summary>
public class RunActionsTests
{
    static readonly DateTimeOffset Now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static RunView View(long id = 5) => new()
    {
        Id = id, Repo = "acme/api", Workflow = "Deploy", Branch = "main", Event = "push",
        Phase = RunPhase.Waiting, CreatedAt = Now, Url = "x",
    };

    static ActionsMonitor Create(FakeGitHub gh, Action<AppConfig>? cfg = null)
    {
        var config = new AppConfig { Repos = ["acme/api", "acme/web"] };
        cfg?.Invoke(config);
        return new ActionsMonitor(config, new GitHubApi("token", "https://api.github.com/", gh), null, "octocat", () => Now);
    }

    static HttpResponseMessage Code(HttpStatusCode code) => new(code) { Content = new StringContent("{}") };

    // ---------- Cancel ----------

    [Fact]
    public async Task Cancel_posts_to_the_cancel_endpoint()
    {
        var gh = new FakeGitHub().Route("repos/acme/api/actions/runs/5/cancel",
            req => Code(req.Method == HttpMethod.Post ? HttpStatusCode.Accepted : HttpStatusCode.MethodNotAllowed));

        Assert.Null(await Create(gh).CancelAsync(View(), Ct));
        Assert.Equal(1, gh.Count("repos/acme/api/actions/runs/5/cancel"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Cancel_failure_returns_a_message_instead_of_throwing(HttpStatusCode code)
    {
        var gh = new FakeGitHub().Status("repos/acme/api/actions/runs/5/cancel", code);

        var error = await Create(gh).CancelAsync(View(), Ct);

        Assert.False(string.IsNullOrEmpty(error));
    }

    // ---------- Approve ----------

    [Fact]
    public async Task Approve_sends_only_the_environments_the_user_may_approve()
    {
        string? body = null;
        var gh = new FakeGitHub().Route("repos/acme/api/actions/runs/5/pending_deployments", req =>
        {
            if (req.Method == HttpMethod.Get)
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                        [{"environment":{"id":11,"name":"production"},"current_user_can_approve":true},
                         {"environment":{"id":12,"name":"vault"},"current_user_can_approve":false}]
                        """, Encoding.UTF8, "application/json"),
                };
            body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") };
        });

        Assert.Null(await Create(gh).ApproveAsync(View(), Ct));
        Assert.NotNull(body);
        Assert.Contains("\"environment_ids\":[11]", body);
        Assert.Contains("\"state\":\"approved\"", body);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""[{"environment":{"id":12,"name":"vault"},"current_user_can_approve":false}]""")]
    public async Task Approve_without_anything_to_approve_returns_a_message_and_posts_nothing(string pending)
    {
        int posts = 0;
        var gh = new FakeGitHub().Route("repos/acme/api/actions/runs/5/pending_deployments", req =>
        {
            if (req.Method == HttpMethod.Post) posts++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(pending, Encoding.UTF8, "application/json") };
        });

        Assert.False(string.IsNullOrEmpty(await Create(gh).ApproveAsync(View(), Ct)));
        Assert.Equal(0, posts);
    }

    // ---------- Filters ----------

    static string Run(long id, string name) => $$$"""
        {"id":{{{id}}},"name":"{{{name}}}","display_title":"t","status":"in_progress","conclusion":null,
         "event":"push","head_branch":"main","head_sha":"abc","workflow_id":7,"created_at":"{{{Now:O}}}",
         "run_started_at":"{{{Now:O}}}","updated_at":"{{{Now:O}}}","html_url":"x","run_attempt":1,"pull_requests":[],
         "actor":{"login":"octocat"},"triggering_actor":{"login":"octocat"}}
        """;

    static FakeGitHub TwoRepos() => new FakeGitHub()
        .Json("repos/acme/api/actions/runs", $$"""{"total_count":2,"workflow_runs":[{{Run(1, "CI")}},{{Run(2, "Nightly")}}]}""")
        .Json("repos/acme/web/actions/runs", $$"""{"total_count":1,"workflow_runs":[{{Run(3, "CI")}}]}""")
        .Json("repos/acme/api/actions/runs/1/jobs", """{"total_count":0,"jobs":[]}""")
        .Json("repos/acme/api/actions/runs/2/jobs", """{"total_count":0,"jobs":[]}""")
        .Json("repos/acme/web/actions/runs/3/jobs", """{"total_count":0,"jobs":[]}""")
        .Json("repos/acme/api/deployments", "[]")
        .Json("repos/acme/web/deployments", "[]");

    [Fact]
    public async Task Hidden_workflow_is_left_out_and_hidden_repo_is_not_polled()
    {
        var gh = TwoRepos();
        var monitor = Create(gh, c =>
        {
            c.HiddenWorkflows = ["acme/api:Nightly"];
            c.HiddenRepos = ["acme/web"];
        });

        await monitor.CycleAsync(Ct);

        Assert.Equal([1L], monitor.BuildSnapshot().Runs.Select(r => r.Id));
        Assert.Equal(0, gh.Count("repos/acme/web/"));
    }

    [Fact]
    public async Task Hiding_takes_effect_on_the_next_cycle_and_unhiding_brings_runs_back()
    {
        var cfg = new AppConfig { Repos = ["acme/api", "acme/web"] };
        var monitor = new ActionsMonitor(cfg, new GitHubApi("token", "https://api.github.com/", TwoRepos()), null, "octocat", () => Now);

        await monitor.CycleAsync(Ct);
        Assert.Equal(3, monitor.BuildSnapshot().Runs.Count);

        cfg.HiddenRepos = ["acme/web"];
        monitor.Kick();
        await monitor.CycleAsync(Ct);
        Assert.Equal([1L, 2L], monitor.BuildSnapshot().Runs.Select(r => r.Id).Order());

        cfg.HiddenRepos = [];
        monitor.Kick();
        await monitor.CycleAsync(Ct);
        Assert.Equal(3, monitor.BuildSnapshot().Runs.Count);
    }

    [Fact]
    public void Workflow_key_and_visibility_ignore_case()
    {
        var cfg = new AppConfig { HiddenWorkflows = ["Acme/API:ci"] };

        Assert.False(RunFilter.Visible(cfg, "acme/api", "CI"));
        Assert.True(RunFilter.Visible(cfg, "acme/api", "Deploy"));
        Assert.Equal(1, RunFilter.HiddenCount(cfg));
    }
}
