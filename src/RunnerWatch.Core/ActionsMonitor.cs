using System.Net;
using System.Text.RegularExpressions;

namespace RunnerWatch.Core;

/// <summary>
/// Polling loop. One call per repo per cycle (runs list), plus jobs/deployments only for active runs.
/// Everything goes through ETags, so cycles without changes barely touch the rate limit.
/// Polling (not webhooks) on purpose: a desktop app has no public endpoint to receive webhooks.
/// Runs on a single Task: the dictionaries below need no locking.
/// </summary>
public sealed class ActionsMonitor : ISnapshotSource
{
    static readonly HashSet<string> QueuedStates = new(StringComparer.OrdinalIgnoreCase) { "queued", "pending", "requested", "waiting" };

    readonly AppConfig _cfg;
    readonly GitHubApi _api;
    readonly MetricsCollector? _metrics;
    readonly string _login;
    readonly Regex _deployRx;
    readonly Func<DateTimeOffset> _now;

    List<string> _repos = new();
    DateTimeOffset _reposAt = DateTimeOffset.MinValue;
    readonly Dictionary<string, DateTimeOffset> _nextPoll = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Dictionary<long, RunView>> _active = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<long, RunView> _failed = new();
    readonly Dictionary<string, string> _repoErrors = new(StringComparer.OrdinalIgnoreCase);

    readonly Dictionary<long, (double? Median, DateTimeOffset At)> _eta = new();
    readonly Dictionary<long, string> _deployEnvByRun = new();
    readonly HashSet<long> _stuckNotified = new();

    readonly HashSet<string> _noRunnerAccess = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, List<Runner>> _runners = new(StringComparer.OrdinalIgnoreCase);
    DateTimeOffset _runnersAt = DateTimeOffset.MinValue;
    readonly Dictionary<string, AgentMetrics> _runnerMetrics = new(StringComparer.OrdinalIgnoreCase);
    DateTimeOffset _metricsAt = DateTimeOffset.MinValue;
    DateTimeOffset _pruneAt;

    string? _error;

    public event Action<Snapshot>? Updated;
    public event Action<string, string, NotifyKind>? Notify;
    public event Action? AuthFailed;

    public ActionsMonitor(AppConfig cfg, GitHubApi api, MetricsCollector? metrics, string login, Func<DateTimeOffset>? clock = null)
    {
        _cfg = cfg;
        _api = api;
        _metrics = metrics;
        _login = login;
        _now = clock ?? (() => DateTimeOffset.UtcNow);
        _pruneAt = _now();
        try { _deployRx = new Regex(cfg.DeployPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)); }
        catch (ArgumentException) { _deployRx = new Regex("deploy|release|publish|prod", RegexOptions.IgnoreCase); }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TimeSpan delay;
            try { delay = await CycleAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (GitHubException ex) when (ex.Status == HttpStatusCode.Unauthorized)
            {
                _error = L.T("error.tokenRejected");
                Log.Warn(_error);
                Publish();
                AuthFailed?.Invoke();
                return;
            }

            Publish();
            try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>One polling cycle. Returns how long to wait before the next one. 401 propagates.</summary>
    internal async Task<TimeSpan> CycleAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(2);
        try
        {
            var now = _now();
            if (_repos.Count == 0 || now - _reposAt > TimeSpan.FromMinutes(10))
                await RefreshReposAsync(ct);

            // Runners before runs: the "stuck" check needs to know whether a runner is busy/online.
            if (_now() - _runnersAt > TimeSpan.FromSeconds(60))
                await RefreshRunnersAsync(ct);

            foreach (var repo in _repos.Where(r => now >= _nextPoll.GetValueOrDefault(r)).ToList())
            {
                bool anyActive = false;
                try
                {
                    anyActive = await PollRepoAsync(repo, ct);
                    _repoErrors.Remove(repo);
                }
                catch (GitHubException ex) when (ex is not RateLimitException && ex.Status != HttpStatusCode.Unauthorized)
                {
                    Log.Warn($"Polling {repo} failed: {ex.Message}");
                    _repoErrors[repo] = ex.Status == HttpStatusCode.NotFound ? L.F("error.repoNotFound", repo) : $"{repo}: {ex.Message}";
                }
                _nextPoll[repo] = _now().AddSeconds(anyActive ? _cfg.ActivePollSeconds : _cfg.IdlePollSeconds);
            }

            if (_metrics != null && _now() - _metricsAt > TimeSpan.FromSeconds(5))
                await RefreshMetricsAsync(ct);

            var failLimit = _now().AddMinutes(-_cfg.ShowFailuresMinutes);
            foreach (var id in _failed.Where(kv => kv.Value.FinishedAt < failLimit).Select(kv => kv.Key).ToList())
                _failed.Remove(id);

            if (_now() - _pruneAt > TimeSpan.FromMinutes(15))
            {
                _api.Prune(TimeSpan.FromMinutes(30));
                _pruneAt = _now();
            }
            _error = null;
        }
        catch (RateLimitException rl)
        {
            _error = L.F("error.rateLimit", rl.Reset.ToLocalTime().ToString("HH:mm"));
            Log.Warn(_error);
            delay = Clamp(rl.Reset - _now(), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(15));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _error = L.F("error.offline", new Uri(_cfg.ApiBaseUrl).Host);
            Log.Warn(_error, ex);
            delay = TimeSpan.FromSeconds(15);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !(ex is GitHubException { Status: HttpStatusCode.Unauthorized }))
        {
            _error = L.F("error.generic", ex.Message);
            Log.Error("Monitoring cycle failed", ex);
            delay = TimeSpan.FromSeconds(15);
        }
        return delay;
    }

    /// <summary>Forces a fresh query of everything on the next cycle.</summary>
    public void Kick()
    {
        _nextPoll.Clear();
        _runnersAt = DateTimeOffset.MinValue;
    }

    public async Task<string?> RerunFailedAsync(RunView run, CancellationToken ct)
    {
        try
        {
            await _api.PostAsync($"repos/{run.Repo}/actions/runs/{run.Id}/rerun-failed-jobs", ct);
            Log.Info($"Re-run of failed jobs requested: {run.Repo} #{run.Id}");
            _failed.Remove(run.Id);
            _nextPoll.Remove(run.Repo);
            return null;
        }
        catch (GitHubException ex) when (ex.Status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound && ex is not RateLimitException)
        {
            Log.Warn($"Re-run refused for {run.Repo} #{run.Id}: {ex.Message}");
            return L.T("error.rerunForbidden");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error($"Re-run failed for {run.Repo} #{run.Id}", ex);
            return L.F("error.generic", ex.Message);
        }
    }

    async Task RefreshReposAsync(CancellationToken ct)
    {
        if (_cfg.Repos.Count > 0)
        {
            _repos = _cfg.Repos.Where(r => r.Contains('/')).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        else
        {
            var list = await _api.GetAsync(
                $"user/repos?affiliation=owner,collaborator,organization_member&sort=pushed&direction=desc&per_page={_cfg.AutoRepoCount}",
                GitHubJson.Default.ListRepo, ct) ?? new();
            _repos = list.Where(r => !r.Archived).Select(r => r.FullName).ToList();
            Log.Info("Watching repos: " + string.Join(", ", _repos));
        }
        _reposAt = _now();

        foreach (var gone in _active.Keys.Except(_repos, StringComparer.OrdinalIgnoreCase).ToList())
            _active.Remove(gone);
    }

    async Task<bool> PollRepoAsync(string repo, CancellationToken ct)
    {
        var res = await _api.GetAsync($"repos/{repo}/actions/runs?per_page=30", GitHubJson.Default.RunsResponse, ct);
        var runs = res?.WorkflowRuns ?? new();
        var activeRuns = runs.Where(r => !IsCompleted(r.Status) && Mine(r)).ToList();

        var prev = _active.GetValueOrDefault(repo) ?? new();
        var next = new Dictionary<long, RunView>();

        foreach (var run in activeRuns)
            next[run.Id] = await BuildRunViewAsync(repo, run, ct);

        // Runs that left the active list: finished (or fell off the 30-run page).
        foreach (var (id, old) in prev)
        {
            if (next.ContainsKey(id)) continue;
            var run = runs.FirstOrDefault(r => r.Id == id);
            if (run == null)
            {
                try { run = await _api.GetAsync($"repos/{repo}/actions/runs/{id}", GitHubJson.Default.WorkflowRun, ct); }
                catch (GitHubException ex) when (ex.Status == HttpStatusCode.NotFound) { run = null; }
            }

            if (run != null && !IsCompleted(run.Status))
                next[id] = await BuildRunViewAsync(repo, run, ct);
            else
                OnFinished(old, run);
        }

        // "Stuck" for real = nobody is picking the job up. If a self-hosted runner of this repo is busy
        // with another job, the wait is a normal queue (capacity), not an offline runner: no alarm.
        bool selfHostedBusy = next.Values.Any(v => v.Jobs.Any(j => j.SelfHosted && j.Status == "in_progress"))
                              || _runners.GetValueOrDefault(repo)?.Any(r => r.Busy) == true;
        foreach (var v in next.Values)
        {
            if (v.Stuck && selfHostedBusy && v.Jobs.Where(j => j.Stuck).All(j => j.SelfHosted))
            {
                v.Stuck = false;
                v.RunnerBusy = true;
            }
            if (v.Stuck && _stuckNotified.Add(v.Id))
            {
                var sh = v.Jobs.Any(j => j.SelfHosted) && !AnySelfHostedOnline(repo) ? L.T("notify.noSelfHosted") : "";
                Log.Warn($"Run stuck in queue: {repo} · {v.Workflow} (#{v.Id})");
                Notify?.Invoke(L.T("notify.stuckTitle"), L.F("notify.stuckBody", Short(repo), v.Workflow, _cfg.StuckQueueMinutes, sh), NotifyKind.Warning);
            }
        }

        foreach (var id in next.Keys) _failed.Remove(id); // a failed run that was re-run is active again

        _active[repo] = next;
        return next.Count > 0;
    }

    async Task<RunView> BuildRunViewAsync(string repo, WorkflowRun run, CancellationToken ct)
    {
        var now = _now();
        var phase = run.Status.Equals("in_progress", StringComparison.OrdinalIgnoreCase) ? RunPhase.Running
            : run.Status.Equals("waiting", StringComparison.OrdinalIgnoreCase) ? RunPhase.Waiting
            : RunPhase.Queued;

        var jobs = new List<Job>();
        try { jobs = (await _api.GetAsync($"repos/{repo}/actions/runs/{run.Id}/jobs?per_page=100", GitHubJson.Default.JobsResponse, ct))?.Jobs ?? new(); }
        catch (GitHubException ex) when (ex is not RateLimitException && ex.Status != HttpStatusCode.Unauthorized) { }

        var selfHostedNames = new HashSet<string>(
            _runners.GetValueOrDefault(repo)?.Select(r => r.Name) ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var stuckLimit = TimeSpan.FromMinutes(_cfg.StuckQueueMinutes);

        var jobViews = new List<JobView>();
        foreach (var j in jobs.Where(j => !IsCompleted(j.Status)))
        {
            bool self = j.Labels?.Any(l => l.Equals("self-hosted", StringComparison.OrdinalIgnoreCase)) == true
                        || (j.RunnerName != null && selfHostedNames.Contains(j.RunnerName));
            bool queued = QueuedStates.Contains(j.Status);
            jobViews.Add(new JobView
            {
                Name = j.Name,
                Status = j.Status,
                RunnerName = string.IsNullOrEmpty(j.RunnerName) ? null : j.RunnerName,
                SelfHosted = self,
                Label = j.Labels?.FirstOrDefault(l => !l.Equals("self-hosted", StringComparison.OrdinalIgnoreCase)),
                Stuck = queued && now - (j.CreatedAt ?? run.CreatedAt) > stuckLimit,
                Metrics = j.RunnerName != null && _runnerMetrics.TryGetValue(j.RunnerName, out var m) ? m : null,
            });
        }

        // The API can keep a run "queued" while one of its jobs is already running.
        if (phase == RunPhase.Queued && jobViews.Any(j => j.Status == "in_progress"))
            phase = RunPhase.Running;

        bool runStuck = jobViews.Any(j => j.Stuck)
                        || (phase == RunPhase.Queued && jobViews.Count == 0 && now - run.CreatedAt > stuckLimit);

        // Deploy: by name (regex) or by a deployment created for this commit after the run started.
        bool isDeploy = _deployRx.IsMatch(run.Name) || _deployRx.IsMatch(run.DisplayTitle ?? "")
                        || jobs.Any(j => _deployRx.IsMatch(j.Name));
        if (!_deployEnvByRun.TryGetValue(run.Id, out var env))
        {
            try
            {
                var deps = await _api.GetAsync($"repos/{repo}/deployments?sha={run.HeadSha}&per_page=5", GitHubJson.Default.ListDeployment, ct);
                var d = deps?.FirstOrDefault(x => x.CreatedAt >= run.CreatedAt.AddMinutes(-1));
                if (d != null) _deployEnvByRun[run.Id] = env = d.Environment;
            }
            catch (GitHubException ex) when (ex is not RateLimitException && ex.Status != HttpStatusCode.Unauthorized) { }
        }
        if (env != null) isDeploy = true;

        double? eta = phase == RunPhase.Running ? await GetEtaAsync(repo, run.WorkflowId, ct) : null;

        return new RunView
        {
            Id = run.Id,
            Repo = repo,
            Workflow = run.Name,
            Title = run.DisplayTitle,
            Branch = BranchText(run),
            Event = run.Event,
            Actor = run.TriggeringActor?.Login ?? run.Actor?.Login,
            Phase = phase,
            IsDeploy = isDeploy,
            DeployEnv = env,
            CreatedAt = run.CreatedAt,
            StartedAt = phase == RunPhase.Running
                ? run.RunStartedAt ?? jobs.Where(j => j.StartedAt != null).Min(j => j.StartedAt) ?? run.CreatedAt
                : null,
            EtaSeconds = eta,
            Url = run.HtmlUrl,
            Stuck = runStuck,
            Jobs = jobViews,
        };
    }

    static string BranchText(WorkflowRun run)
    {
        var pr = run.PullRequests?.FirstOrDefault();
        return pr != null ? $"{pr.Head.Ref} → {pr.Base.Ref}  (PR #{pr.Number})" : run.HeadBranch ?? "-";
    }

    async Task<double?> GetEtaAsync(string repo, long workflowId, CancellationToken ct)
    {
        if (_eta.TryGetValue(workflowId, out var c) && _now() - c.At < TimeSpan.FromMinutes(30))
            return c.Median;
        double? median = null;
        try
        {
            var r = await _api.GetAsync($"repos/{repo}/actions/workflows/{workflowId}/runs?status=success&per_page=10", GitHubJson.Default.RunsResponse, ct);
            var durations = (r?.WorkflowRuns ?? new())
                .Where(x => x.RunStartedAt != null)
                .Select(x => (x.UpdatedAt - x.RunStartedAt!.Value).TotalSeconds)
                .Where(s => s > 0 && s < 6 * 3600)
                .OrderBy(s => s).ToList();
            // Median: one run stuck for 2h doesn't ruin the estimate.
            if (durations.Count >= 2) median = durations[durations.Count / 2];
        }
        catch (GitHubException ex) when (ex is not RateLimitException && ex.Status != HttpStatusCode.Unauthorized) { }
        _eta[workflowId] = (median, _now());
        return median;
    }

    async Task RefreshRunnersAsync(CancellationToken ct)
    {
        foreach (var repo in _repos.Where(r => !_noRunnerAccess.Contains(r)))
        {
            try
            {
                var r = await _api.GetAsync($"repos/{repo}/actions/runners?per_page=100", GitHubJson.Default.RunnersResponse, ct);
                var list = r?.Runners ?? new();
                if (list.Count > 0) _runners[repo] = list; else _runners.Remove(repo);
            }
            catch (GitHubException ex) when (ex.Status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound && ex is not RateLimitException)
            {
                // Listing runners needs admin on the repo. Without it, don't insist.
                if (_noRunnerAccess.Add(repo)) Log.Info($"No permission to list runners of {repo} (needs admin)");
            }
        }
        foreach (var gone in _runners.Keys.Except(_repos, StringComparer.OrdinalIgnoreCase).ToList())
            _runners.Remove(gone);
        _runnersAt = _now();
    }

    async Task RefreshMetricsAsync(CancellationToken ct)
    {
        _metricsAt = _now();
        // Runners from the runners API (needs admin) plus the self-hosted runners seen in running jobs.
        var names = _runners.Values.SelectMany(x => x)
            .Where(r => r.Status.Equals("online", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Name)
            .Concat(_active.Values.SelectMany(d => d.Values).SelectMany(v => v.Jobs)
                .Where(j => j.SelfHosted && j.RunnerName != null).Select(j => j.RunnerName!))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var results = await Task.WhenAll(names.Select(async n => (n, m: await _metrics!.GetAsync(n, ct))));
        _runnerMetrics.Clear();
        foreach (var (n, m) in results)
            if (m != null) _runnerMetrics[n] = m;
    }

    void OnFinished(RunView old, WorkflowRun? run)
    {
        _stuckNotified.Remove(old.Id);
        _deployEnvByRun.Remove(old.Id);
        if (run == null) return;

        bool failed = run.Conclusion is "failure" or "timed_out" or "startup_failure";
        if (failed && _cfg.ShowFailuresMinutes > 0)
        {
            var f = old.With(new List<JobView>());
            f.Phase = RunPhase.Failed;
            f.Stuck = false;
            f.RunnerBusy = false;
            f.FinishedAt = run.UpdatedAt;
            _failed[old.Id] = f;
        }

        if (!_cfg.Notifications) return;
        var (verb, kind) = run.Conclusion switch
        {
            "success" => (L.T("notify.succeeded"), NotifyKind.Success),
            "failure" or "timed_out" or "startup_failure" => (L.T("notify.failed"), NotifyKind.Failure),
            "cancelled" => (L.T("notify.cancelled"), NotifyKind.Info),
            _ => (run.Conclusion ?? L.T("notify.finished"), NotifyKind.Info),
        };
        // Only deploy successes are notified (plain CI success is noise); failures always are.
        if (kind is NotifyKind.Success or NotifyKind.Info && !old.IsDeploy) return;

        var dur = run.RunStartedAt is { } s ? Fmt(run.UpdatedAt - s) : "";
        var what = old.IsDeploy ? $"{L.T("notify.deploy")}{(old.DeployEnv != null ? " " + old.DeployEnv : "")}" : old.Workflow;
        Notify?.Invoke($"{what} {verb}", $"{Short(old.Repo)} · {old.Branch}  {dur}".Trim(), kind);
    }

    internal Snapshot BuildSnapshot()
    {
        var runs = _active.Values.SelectMany(d => d.Values)
            .Select(WithFreshMetrics)
            .Concat(_failed.Values.Where(f => !_active.Values.Any(d => d.ContainsKey(f.Id))))
            .OrderBy(r => r.Phase switch { RunPhase.Running => 0, RunPhase.Failed => 2, _ => 1 })
            .ThenByDescending(r => r.IsDeploy)
            .ThenBy(r => r.CreatedAt)
            .ToList();

        var runners = _runners
            .SelectMany(kv => kv.Value.Select(r => new RunnerView
            {
                Name = r.Name,
                Repo = kv.Key,
                Status = r.Status,
                Busy = r.Busy,
                Os = r.Os,
                Metrics = _runnerMetrics.GetValueOrDefault(r.Name),
            }))
            .GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase) // same machine registered in several repos
            .Select(g => g.OrderByDescending(r => r.Busy).First())
            .OrderBy(r => r.Status == "online" ? 0 : 1).ThenBy(r => r.Name)
            .ToList();

        return new Snapshot
        {
            Runs = runs,
            Runners = runners,
            RepoCount = _repos.Count,
            RateRemaining = _api.RateRemaining,
            Error = _error,
            RepoErrors = _repoErrors.Values.ToList(),
        };
    }

    void Publish() => Updated?.Invoke(BuildSnapshot());

    // Metrics change every 5 s, jobs only when GitHub changes: merge both at publish time.
    RunView WithFreshMetrics(RunView r)
    {
        if (r.Jobs.Count == 0 || _runnerMetrics.Count == 0) return r;
        return r.With(r.Jobs.Select(j => j.RunnerName != null && _runnerMetrics.TryGetValue(j.RunnerName, out var m)
            ? new JobView { Name = j.Name, Status = j.Status, RunnerName = j.RunnerName, SelfHosted = j.SelfHosted, Label = j.Label, Stuck = j.Stuck, Metrics = m }
            : j).ToList());
    }

    bool AnySelfHostedOnline(string repo) =>
        _runners.GetValueOrDefault(repo)?.Any(r => r.Status.Equals("online", StringComparison.OrdinalIgnoreCase)) == true;

    bool Mine(WorkflowRun r) =>
        !_cfg.OnlyMine || string.Equals(r.TriggeringActor?.Login ?? r.Actor?.Login, _login, StringComparison.OrdinalIgnoreCase);

    static bool IsCompleted(string status) => status.Equals("completed", StringComparison.OrdinalIgnoreCase);
    static string Short(string repo) => repo.Contains('/') ? repo[(repo.IndexOf('/') + 1)..] : repo;
    static TimeSpan Clamp(TimeSpan v, TimeSpan min, TimeSpan max) => v < min ? min : v > max ? max : v;

    public static string Fmt(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}h{t.Minutes:00}m" : $"{t.Minutes}m{t.Seconds:00}s";
    }
}
