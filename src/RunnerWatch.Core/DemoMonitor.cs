namespace RunnerWatch.Core;

/// <summary>
/// <c>--demo</c>: a scripted, looping scenario with fake data (acme/*). No network, no token.
/// Used for screenshots/GIFs and for contributors who want to work on the UI without a GitHub setup.
/// </summary>
public sealed class DemoMonitor : ISnapshotSource
{
    const double LoopSeconds = 45;
    readonly Func<DateTimeOffset> _now;
    readonly DateTimeOffset _t0;
    readonly HashSet<int> _notifiedLoops = new();
    bool _rerunRequested;

    public event Action<Snapshot>? Updated;
    public event Action<string, string, NotifyKind>? Notify;
    public event Action? AuthFailed { add { } remove { } }

    public DemoMonitor(Func<DateTimeOffset>? clock = null)
    {
        _now = clock ?? (() => DateTimeOffset.UtcNow);
        _t0 = _now();
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Updated?.Invoke(Build());
            try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { return; }
        }
    }

    public void Kick() => Updated?.Invoke(Build());

    public Task<string?> RerunFailedAsync(RunView run, CancellationToken ct)
    {
        _rerunRequested = true;
        return Task.FromResult<string?>(null);
    }

    internal Snapshot Build()
    {
        var now = _now();
        var elapsed = (now - _t0).TotalSeconds;
        int loop = (int)(elapsed / LoopSeconds);
        double t = elapsed % LoopSeconds;
        var loopStart = now.AddSeconds(-t);
        double cpu = Math.Round(62 + 23 * Math.Sin(elapsed / 3.0), 1);
        var buildMetrics = new AgentMetrics(cpu, (long)(9.4 * 1073741824), 16L * 1073741824, "build-01");

        var runs = new List<RunView>
        {
            new()
            {
                Id = 1, Repo = "acme/api", Workflow = "CI", Title = "Add rate limiting to /v2/orders",
                Branch = "feat/rate-limit → main  (PR #482)", Event = "pull_request", Actor = "octocat",
                Phase = RunPhase.Running, CreatedAt = loopStart.AddSeconds(-140), StartedAt = loopStart.AddSeconds(-130),
                EtaSeconds = 270, Url = "https://github.com/", RunnerBusy = true,
                Jobs =
                [
                    new() { Name = "build", Status = "in_progress", RunnerName = "build-01", SelfHosted = true, Metrics = buildMetrics },
                    new() { Name = "integration-tests", Status = "queued", SelfHosted = true, Label = "linux", Stuck = true },
                ],
            },
        };

        // Production deploy: finishes 20 s into each loop, then a success notification.
        if (t < 20)
        {
            runs.Add(new()
            {
                Id = 2, Repo = "acme/web", Workflow = "Deploy", Title = "Release 3.14.0", Branch = "main", Event = "push",
                Actor = "hubot", Phase = RunPhase.Running, IsDeploy = true, DeployEnv = "production",
                CreatedAt = loopStart.AddSeconds(-105), StartedAt = loopStart.AddSeconds(-100), EtaSeconds = 120,
                Url = "https://github.com/",
                Jobs = [new() { Name = "deploy", Status = "in_progress", RunnerName = "GitHub Actions 12", Label = "ubuntu-latest" }],
            });
        }
        else if (_notifiedLoops.Add(loop))
        {
            Notify?.Invoke($"{L.T("notify.deploy")} production {L.T("notify.succeeded")}", "web · main  2m00s", NotifyKind.Success);
        }

        // A PR waits in the queue, then a hosted runner picks it up.
        bool started = t >= 12;
        runs.Add(new()
        {
            Id = 3, Repo = "acme/mobile", Workflow = "CI", Title = "Dark mode for settings",
            Branch = "feat/dark-settings → main  (PR #77)", Event = "pull_request", Actor = "monalisa",
            Phase = started ? RunPhase.Running : RunPhase.Queued, CreatedAt = loopStart.AddSeconds(-25),
            StartedAt = started ? loopStart.AddSeconds(12) : null, EtaSeconds = started ? 95 : null, Url = "https://github.com/",
            Jobs = [new() { Name = "android", Status = started ? "in_progress" : "queued", Label = "ubuntu-latest", RunnerName = started ? "GitHub Actions 31" : null }],
        });

        // A release waiting for a self-hosted macOS runner that is offline: a real STUCK.
        runs.Add(new()
        {
            Id = 4, Repo = "acme/cli", Workflow = "Release", Title = "v2.0.0", Branch = "v2.0.0", Event = "push", Actor = "octocat",
            Phase = RunPhase.Queued, IsDeploy = true, CreatedAt = loopStart.AddSeconds(-460), Url = "https://github.com/", Stuck = true,
            Jobs = [new() { Name = "notarize", Status = "queued", SelfHosted = true, Label = "macOS", Stuck = true }],
        });

        if (!_rerunRequested)
        {
            runs.Add(new()
            {
                Id = 5, Repo = "acme/api", Workflow = "Nightly e2e", Title = "Nightly e2e", Branch = "main", Event = "schedule",
                Actor = "github-actions", Phase = RunPhase.Failed, CreatedAt = loopStart.AddMinutes(-9),
                FinishedAt = loopStart.AddMinutes(-3), Url = "https://github.com/",
            });
        }

        return new Snapshot
        {
            Runs = runs
                .OrderBy(r => r.Phase switch { RunPhase.Running => 0, RunPhase.Failed => 2, _ => 1 })
                .ThenByDescending(r => r.IsDeploy).ThenBy(r => r.CreatedAt).ToList(),
            Runners =
            [
                new() { Name = "build-01", Repo = "acme/api", Status = "online", Busy = true, Os = "Linux", Metrics = buildMetrics },
                new() { Name = "mac-mini", Repo = "acme/cli", Status = "offline", Os = "macOS" },
            ],
            RepoCount = 4,
            RateRemaining = 4980,
            Error = null,
            At = now.ToLocalTime(),
        };
    }
}
