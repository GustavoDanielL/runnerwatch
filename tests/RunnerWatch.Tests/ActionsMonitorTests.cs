using RunnerWatch.Core;

namespace RunnerWatch.Tests;

public class ActionsMonitorTests
{
    static readonly DateTimeOffset Now = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);

    static string Run(long id, string status, DateTimeOffset created, string name = "CI", string? conclusion = null) => $$$"""
        {"id":{{{id}}},"name":"{{{name}}}","display_title":"t","status":"{{{status}}}","conclusion":{{{(conclusion == null ? "null" : $"\"{conclusion}\"")}}},
         "event":"push","head_branch":"main","head_sha":"abc","workflow_id":7,"created_at":"{{{created:O}}}",
         "run_started_at":"{{{created:O}}}","updated_at":"{{{Now:O}}}","html_url":"https://github.com/acme/api/actions/runs/{{{id}}}",
         "run_attempt":1,"pull_requests":[],"actor":{"login":"octocat"},"triggering_actor":{"login":"octocat"}}
        """;

    static string Job(string name, string status, DateTimeOffset created, string? runner = null, bool selfHosted = true) => $$$"""
        {"id":1,"name":"{{{name}}}","status":"{{{status}}}","conclusion":null,"created_at":"{{{created:O}}}","started_at":null,
         "runner_name":{{{(runner == null ? "null" : $"\"{runner}\"")}}},"labels":[{{{(selfHosted ? "\"self-hosted\",\"linux\"" : "\"ubuntu-latest\"")}}}],"html_url":"x"}
        """;

    static (ActionsMonitor monitor, FakeGitHub gh, List<(string, string, NotifyKind)> notes) Create(FakeGitHub gh, Action<AppConfig>? cfg = null)
    {
        var config = new AppConfig { Repos = ["acme/api"], StuckQueueMinutes = 5, Notifications = true };
        cfg?.Invoke(config);
        var api = new GitHubApi("token", "https://api.github.com/", gh);
        var monitor = new ActionsMonitor(config, api, metrics: null, login: "octocat", clock: () => Now);
        var notes = new List<(string, string, NotifyKind)>();
        monitor.Notify += (t, m, k) => notes.Add((t, m, k));
        return (monitor, gh, notes);
    }

    [Fact]
    public async Task Queue_behind_a_busy_self_hosted_runner_is_RunnerBusy_not_stuck()
    {
        // backend runs on the only self-hosted runner; frontend has been waiting 16 min for it.
        var gh = new FakeGitHub()
            .Json("repos/acme/api/actions/runs", $$"""{"total_count":1,"workflow_runs":[{{Run(1, "queued", Now.AddMinutes(-16))}}]}""")
            .Json("repos/acme/api/actions/runs/1/jobs", $$"""{"total_count":2,"jobs":[{{Job("backend", "in_progress", Now.AddMinutes(-16), "box")}},{{Job("frontend", "queued", Now.AddMinutes(-16))}}]}""")
            .Json("repos/acme/api/deployments", "[]");
        var (monitor, _, notes) = Create(gh);

        await monitor.CycleAsync(TestContext.Current.CancellationToken);
        var run = Assert.Single(monitor.BuildSnapshot().Runs);

        Assert.False(run.Stuck);
        Assert.True(run.RunnerBusy);
        Assert.Equal(RunPhase.Running, run.Phase); // API says "queued", but a job is running
        Assert.Empty(notes);                        // capacity is not an outage: no alarm
    }

    [Fact]
    public async Task Queue_with_no_runner_picking_up_is_stuck_and_notifies_once()
    {
        var gh = new FakeGitHub()
            .Json("repos/acme/api/actions/runs", $$"""{"total_count":1,"workflow_runs":[{{Run(1, "queued", Now.AddMinutes(-9))}}]}""")
            .Json("repos/acme/api/actions/runs/1/jobs", $$"""{"total_count":1,"jobs":[{{Job("build", "queued", Now.AddMinutes(-9))}}]}""")
            .Json("repos/acme/api/deployments", "[]");
        var (monitor, _, notes) = Create(gh);

        await monitor.CycleAsync(TestContext.Current.CancellationToken);
        monitor.Kick();
        await monitor.CycleAsync(TestContext.Current.CancellationToken);

        var run = Assert.Single(monitor.BuildSnapshot().Runs);
        Assert.True(run.Stuck);
        Assert.False(run.RunnerBusy);
        Assert.Single(notes);
        Assert.Equal(NotifyKind.Warning, notes[0].Item3);
    }

    [Fact]
    public async Task Short_queue_is_not_stuck()
    {
        var gh = new FakeGitHub()
            .Json("repos/acme/api/actions/runs", $$"""{"total_count":1,"workflow_runs":[{{Run(1, "queued", Now.AddMinutes(-2))}}]}""")
            .Json("repos/acme/api/actions/runs/1/jobs", $$"""{"total_count":1,"jobs":[{{Job("build", "queued", Now.AddMinutes(-2))}}]}""")
            .Json("repos/acme/api/deployments", "[]");
        var (monitor, _, _) = Create(gh);

        await monitor.CycleAsync(TestContext.Current.CancellationToken);

        Assert.False(Assert.Single(monitor.BuildSnapshot().Runs).Stuck);
    }

    [Fact]
    public async Task Deploy_is_detected_by_workflow_name()
    {
        var gh = new FakeGitHub()
            .Json("repos/acme/api/actions/runs", $$"""{"total_count":1,"workflow_runs":[{{Run(1, "in_progress", Now.AddMinutes(-1), name: "Deploy production")}}]}""")
            .Json("repos/acme/api/actions/runs/1/jobs", """{"total_count":0,"jobs":[]}""")
            .Json("repos/acme/api/deployments", "[]")
            .Json("repos/acme/api/actions/workflows/7/runs", """{"total_count":0,"workflow_runs":[]}""");
        var (monitor, _, _) = Create(gh);

        await monitor.CycleAsync(TestContext.Current.CancellationToken);

        Assert.True(Assert.Single(monitor.BuildSnapshot().Runs).IsDeploy);
    }

    [Fact]
    public async Task Failed_run_stays_visible_with_failed_phase_and_notifies()
    {
        var running = $$"""{"total_count":1,"workflow_runs":[{{Run(1, "in_progress", Now.AddMinutes(-3))}}]}""";
        var failed = $$"""{"total_count":1,"workflow_runs":[{{Run(1, "completed", Now.AddMinutes(-3), conclusion: "failure")}}]}""";
        var current = running;
        var gh = new FakeGitHub()
            .Route("repos/acme/api/actions/runs", _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(current) })
            .Json("repos/acme/api/actions/runs/1/jobs", """{"total_count":0,"jobs":[]}""")
            .Json("repos/acme/api/deployments", "[]")
            .Json("repos/acme/api/actions/workflows/7/runs", """{"total_count":0,"workflow_runs":[]}""");
        var (monitor, _, notes) = Create(gh);

        await monitor.CycleAsync(TestContext.Current.CancellationToken);
        current = failed;
        monitor.Kick();
        await monitor.CycleAsync(TestContext.Current.CancellationToken);

        var run = Assert.Single(monitor.BuildSnapshot().Runs);
        Assert.Equal(RunPhase.Failed, run.Phase);
        Assert.Contains(notes, n => n.Item3 == NotifyKind.Failure);
    }

    [Fact]
    public async Task Rerun_posts_to_the_rerun_failed_jobs_endpoint()
    {
        var gh = new FakeGitHub()
            .Route("repos/acme/api/actions/runs/1/rerun-failed-jobs", _ => new HttpResponseMessage(System.Net.HttpStatusCode.Created));
        var (monitor, _, _) = Create(gh);
        var run = new RunView { Id = 1, Repo = "acme/api", Workflow = "CI", Branch = "main", Event = "push", Phase = RunPhase.Failed, CreatedAt = Now, Url = "https://x" };

        var error = await monitor.RerunFailedAsync(run, TestContext.Current.CancellationToken);

        Assert.Null(error);
        Assert.Equal(HttpMethod.Post, Assert.Single(gh.Requests).Method);
    }

    [Fact]
    public async Task Rerun_without_permission_returns_a_message_instead_of_throwing()
    {
        var gh = new FakeGitHub().Status("repos/acme/api/actions/runs/1/rerun-failed-jobs", System.Net.HttpStatusCode.Forbidden);
        var (monitor, _, _) = Create(gh);
        var run = new RunView { Id = 1, Repo = "acme/api", Workflow = "CI", Branch = "main", Event = "push", Phase = RunPhase.Failed, CreatedAt = Now, Url = "https://x" };

        Assert.NotNull(await monitor.RerunFailedAsync(run, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Missing_repo_becomes_a_repo_error_not_a_crash()
    {
        var (monitor, _, _) = Create(new FakeGitHub());

        await monitor.CycleAsync(TestContext.Current.CancellationToken);

        Assert.Single(monitor.BuildSnapshot().RepoErrors);
    }

    [Fact]
    public async Task Auto_discovery_includes_org_and_collaborator_repos()
    {
        var gh = new FakeGitHub().Json("user/repos", "[]");
        var (monitor, _, _) = Create(gh, c => c.Repos = []);

        await monitor.CycleAsync(TestContext.Current.CancellationToken);

        var query = Assert.Single(gh.Requests).RequestUri!.Query;
        Assert.Contains("affiliation=owner,collaborator,organization_member", Uri.UnescapeDataString(query));
    }

    [Theory]
    [InlineData(0, "0m00s")]
    [InlineData(65, "1m05s")]
    [InlineData(3725, "1h02m")]
    [InlineData(-5, "0m00s")]
    public void Fmt_formats_durations(int seconds, string expected) =>
        Assert.Equal(expected, ActionsMonitor.Fmt(TimeSpan.FromSeconds(seconds)));
}
