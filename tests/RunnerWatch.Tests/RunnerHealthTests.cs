using RunnerWatch.Core;

namespace RunnerWatch.Tests;

/// <summary>Paid (GitHub-hosted) runner alert, runner offline alert and runner memory alert.</summary>
public class RunnerHealthTests
{
    static readonly DateTimeOffset T0 = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    sealed class Clock { public DateTimeOffset Now = T0; }

    static string Run(long id, bool? isPrivate = true)
    {
        var repo = isPrivate is { } p ? $",\"repository\":{{\"private\":{(p ? "true" : "false")}}}" : "";
        return $$"""
            {"id":{{id}},"name":"CI","display_title":"t","status":"in_progress","conclusion":null,
             "event":"push","head_branch":"main","head_sha":"abc","workflow_id":7,"created_at":"{{T0:O}}",
             "run_started_at":"{{T0:O}}","updated_at":"{{T0:O}}","html_url":"x","run_attempt":1,"pull_requests":[],
             "actor":{"login":"octocat"},"triggering_actor":{"login":"octocat"}{{repo}}}
            """;
    }

    const string HostedJob = """
        {"id":1,"name":"build","status":"in_progress","conclusion":null,"created_at":null,"started_at":null,
         "runner_name":"GitHub Actions 7","runner_group_name":"GitHub Actions","labels":["ubuntu-latest"],"html_url":"x"}
        """;

    const string SelfHostedJob = """
        {"id":2,"name":"test","status":"in_progress","conclusion":null,"created_at":null,"started_at":null,
         "runner_name":"box","labels":["self-hosted","linux"],"html_url":"x"}
        """;

    static string Runner(string status) =>
        $$"""{"total_count":1,"runners":[{"id":1,"name":"box","os":"Linux","status":"{{status}}","busy":false,"labels":[]}]}""";

    static FakeGitHub Repo(string jobs, bool? isPrivate = true, string? runners = null)
    {
        var gh = new FakeGitHub()
            .Json("repos/acme/api/actions/runs", $$"""{"total_count":1,"workflow_runs":[{{Run(1, isPrivate)}}]}""")
            .Json("repos/acme/api/actions/runs/1/jobs", $$"""{"total_count":1,"jobs":[{{jobs}}]}""")
            .Json("repos/acme/api/deployments", "[]");
        if (runners != null) gh.Json("repos/acme/api/actions/runners", runners);
        return gh;
    }

    static (ActionsMonitor, List<(string Title, string Body, NotifyKind Kind)>) Create(
        FakeGitHub gh, Clock? clock = null, Action<AppConfig>? cfg = null, string? statePath = null)
    {
        var config = new AppConfig { Repos = ["acme/api"], Notifications = true };
        cfg?.Invoke(config);
        clock ??= new Clock();
        var monitor = new ActionsMonitor(config, new GitHubApi("token", "https://api.github.com/", gh), null, "octocat",
            () => clock.Now, statePath);
        var notes = new List<(string, string, NotifyKind)>();
        monitor.Notify += (t, b, k) => notes.Add((t, b, k));
        return (monitor, notes);
    }

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---------- Paid runner ----------

    [Fact]
    public async Task Hosted_job_in_a_repo_with_self_hosted_runners_is_flagged_and_notified_once()
    {
        var (monitor, notes) = Create(Repo(HostedJob, runners: Runner("online")));

        await monitor.CycleAsync(Ct);
        monitor.Kick();
        await monitor.CycleAsync(Ct);

        var run = Assert.Single(monitor.BuildSnapshot().Runs);
        Assert.True(run.PaidRunner);
        Assert.True(Assert.Single(run.Jobs).PaidRunner);
        var note = Assert.Single(notes);
        Assert.Equal(NotifyKind.Warning, note.Kind);
        Assert.Contains("ubuntu-latest", note.Body);
    }

    [Fact]
    public async Task Mixed_run_learns_self_hosted_and_flags_only_the_hosted_job()
    {
        var (monitor, notes) = Create(Repo(HostedJob + "," + SelfHostedJob));

        await monitor.CycleAsync(Ct);

        var run = Assert.Single(monitor.BuildSnapshot().Runs);
        Assert.True(run.PaidRunner);
        Assert.Equal(["build"], run.Jobs.Where(j => j.PaidRunner).Select(j => j.Name));
        Assert.Single(notes);
    }

    [Fact]
    public async Task Hosted_job_in_a_repo_never_seen_with_self_hosted_is_normal()
    {
        var (monitor, notes) = Create(Repo(HostedJob));

        await monitor.CycleAsync(Ct);

        Assert.False(Assert.Single(monitor.BuildSnapshot().Runs).PaidRunner);
        Assert.Empty(notes);
    }

    [Fact]
    public async Task Public_repo_is_never_flagged_hosted_minutes_are_free()
    {
        var (monitor, notes) = Create(Repo(HostedJob, isPrivate: false, runners: Runner("online")), cfg: c => c.PaidRunnerAlert = "all");

        await monitor.CycleAsync(Ct);

        Assert.False(Assert.Single(monitor.BuildSnapshot().Runs).PaidRunner);
        Assert.Empty(notes);
    }

    [Theory]
    [InlineData("all", true)]
    [InlineData("off", false)]
    public async Task Mode_all_flags_any_private_repo_and_off_disables(string mode, bool flagged)
    {
        var (monitor, notes) = Create(Repo(HostedJob, runners: mode == "off" ? Runner("online") : null), cfg: c => c.PaidRunnerAlert = mode);

        await monitor.CycleAsync(Ct);

        Assert.Equal(flagged, Assert.Single(monitor.BuildSnapshot().Runs).PaidRunner);
        Assert.Equal(flagged ? 1 : 0, notes.Count);
    }

    [Fact]
    public async Task Self_hosted_repos_are_remembered_across_restarts()
    {
        var state = Path.Combine(Path.GetTempPath(), $"rw-state-{Guid.NewGuid():N}.txt");
        try
        {
            var (first, _) = Create(Repo(SelfHostedJob), statePath: state);
            await first.CycleAsync(Ct);

            // After a restart the very first run is already on a hosted runner: still caught.
            var (second, notes) = Create(Repo(HostedJob), statePath: state);
            await second.CycleAsync(Ct);

            Assert.True(Assert.Single(second.BuildSnapshot().Runs).PaidRunner);
            Assert.Single(notes);
        }
        finally { File.Delete(state); }
    }

    [Theory]
    [InlineData(null, new[] { "ubuntu-latest" }, true)]
    [InlineData(null, new[] { "windows-2022" }, true)]
    [InlineData(null, new[] { "macos-14" }, true)]
    [InlineData("GitHub Actions", new[] { "my-large-runner" }, true)]
    [InlineData(null, new[] { "linux", "heavy" }, false)]
    [InlineData("Default", new[] { "self-hosted" }, false)]
    public void Recognises_github_hosted_jobs(string? group, string[] labels, bool hosted) =>
        Assert.Equal(hosted, ActionsMonitor.IsGitHubHosted(new Job(1, "j", "queued", null, null, null, null, [.. labels], "x", group)));

    // ---------- Runner offline ----------

    [Fact]
    public async Task Runner_that_goes_offline_notifies_after_the_limit_and_again_when_back()
    {
        var clock = new Clock();
        var gh = Repo(SelfHostedJob, runners: Runner("online"));
        var (monitor, notes) = Create(gh, clock);
        await monitor.CycleAsync(Ct);

        gh.Json("repos/acme/api/actions/runners", Runner("offline"));
        clock.Now = T0.AddMinutes(1);
        monitor.Kick();
        await monitor.CycleAsync(Ct);
        clock.Now = T0.AddMinutes(3);
        monitor.Kick();
        await monitor.CycleAsync(Ct);
        Assert.Empty(notes); // offline for 2 min only

        clock.Now = T0.AddMinutes(4);
        monitor.Kick();
        await monitor.CycleAsync(Ct);
        clock.Now = T0.AddMinutes(5);
        monitor.Kick();
        await monitor.CycleAsync(Ct);
        var offline = Assert.Single(notes);
        Assert.Equal(NotifyKind.Warning, offline.Kind);
        Assert.Contains("box", offline.Body);

        gh.Json("repos/acme/api/actions/runners", Runner("online"));
        clock.Now = T0.AddMinutes(6);
        monitor.Kick();
        await monitor.CycleAsync(Ct);
        Assert.Equal(2, notes.Count);
        Assert.Equal(NotifyKind.Success, notes[1].Kind);
    }

    [Fact]
    public async Task Runner_already_offline_at_startup_stays_quiet()
    {
        var clock = new Clock();
        var (monitor, notes) = Create(Repo(SelfHostedJob, runners: Runner("offline")), clock);

        await monitor.CycleAsync(Ct);
        clock.Now = T0.AddHours(1);
        monitor.Kick();
        await monitor.CycleAsync(Ct);

        Assert.Empty(notes);
    }

    // ---------- Memory ----------

    static AgentMetrics Mem(double gbUsed) => new(10, (long)(gbUsed * 1073741824), 16L * 1073741824, "box");

    [Fact]
    public void High_memory_notifies_after_two_minutes_once_and_rearms_after_it_drops()
    {
        var clock = new Clock();
        var (monitor, notes) = Create(new FakeGitHub(), clock);

        monitor.ObserveMetrics([("box", Mem(15.2))]); // 95%
        clock.Now = T0.AddMinutes(1);
        monitor.ObserveMetrics([("box", Mem(15.2))]);
        Assert.Empty(notes);

        clock.Now = T0.AddMinutes(2);
        monitor.ObserveMetrics([("box", Mem(15.2))]);
        clock.Now = T0.AddMinutes(3);
        monitor.ObserveMetrics([("box", Mem(15.2))]);
        var note = Assert.Single(notes);
        Assert.Contains("95", note.Body);

        // Hovering just under the limit doesn't re-arm; a real drop does.
        clock.Now = T0.AddMinutes(4);
        monitor.ObserveMetrics([("box", Mem(14.0))]); // 87.5%
        clock.Now = T0.AddMinutes(5);
        monitor.ObserveMetrics([("box", Mem(15.2))]);
        clock.Now = T0.AddMinutes(8);
        monitor.ObserveMetrics([("box", Mem(15.2))]);
        Assert.Single(notes);

        clock.Now = T0.AddMinutes(9);
        monitor.ObserveMetrics([("box", Mem(8))]); // 50%
        clock.Now = T0.AddMinutes(10);
        monitor.ObserveMetrics([("box", Mem(15.2))]);
        clock.Now = T0.AddMinutes(12);
        monitor.ObserveMetrics([("box", Mem(15.2))]);
        Assert.Equal(2, notes.Count);
    }

    [Fact]
    public void Short_memory_spike_is_ignored()
    {
        var clock = new Clock();
        var (monitor, notes) = Create(new FakeGitHub(), clock);

        monitor.ObserveMetrics([("box", Mem(15.5))]);
        clock.Now = T0.AddSeconds(90);
        monitor.ObserveMetrics([("box", Mem(6))]);
        clock.Now = T0.AddMinutes(5);
        monitor.ObserveMetrics([("box", Mem(6))]);

        Assert.Empty(notes);
    }
}
