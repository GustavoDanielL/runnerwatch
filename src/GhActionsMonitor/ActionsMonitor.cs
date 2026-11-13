using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace GhActionsMonitor;

/// <summary>
/// Loop de polling. Uma chamada por repo por ciclo (lista de runs), mais jobs/deployments só dos runs ativos.
/// Tudo via ETag, então ciclos sem mudança praticamente não gastam rate limit.
/// Roda num único Task: os dicionários abaixo não precisam de lock.
/// </summary>
public sealed class ActionsMonitor
{
    static readonly HashSet<string> QueuedStates = new(StringComparer.OrdinalIgnoreCase) { "queued", "pending", "requested", "waiting" };

    readonly AppConfig _cfg;
    readonly GitHubApi _api;
    readonly MetricsCollector _metrics;
    readonly string _login;
    readonly Regex _deployRx;

    List<string> _repos = new();
    DateTimeOffset _reposAt = DateTimeOffset.MinValue;
    readonly Dictionary<string, DateTimeOffset> _nextPoll = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Dictionary<long, RunView>> _active = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> _repoErrors = new(StringComparer.OrdinalIgnoreCase);

    readonly Dictionary<long, (double? Median, DateTimeOffset At)> _eta = new();
    readonly Dictionary<long, string> _deployEnvByRun = new();
    readonly HashSet<long> _stuckNotified = new();

    readonly HashSet<string> _noRunnerAccess = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, List<Runner>> _runners = new(StringComparer.OrdinalIgnoreCase);
    DateTimeOffset _runnersAt = DateTimeOffset.MinValue;
    readonly Dictionary<string, AgentMetrics> _runnerMetrics = new(StringComparer.OrdinalIgnoreCase);
    DateTimeOffset _metricsAt = DateTimeOffset.MinValue;
    DateTimeOffset _pruneAt = DateTimeOffset.UtcNow;

    string? _error;

    public event Action<Snapshot>? Updated;
    public event Action<string, string, NotifyKind>? Notify;
    public event Action? AuthFailed;

    public ActionsMonitor(AppConfig cfg, GitHubApi api, MetricsCollector metrics, string login)
    {
        _cfg = cfg;
        _api = api;
        _metrics = metrics;
        _login = login;
        try { _deployRx = new Regex(cfg.DeployPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)); }
        catch (ArgumentException) { _deployRx = new Regex("deploy|release|publish|prod", RegexOptions.IgnoreCase); }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var delay = TimeSpan.FromSeconds(2);
            try
            {
                var now = DateTimeOffset.UtcNow;
                if (_repos.Count == 0 || now - _reposAt > TimeSpan.FromMinutes(10))
                    await RefreshReposAsync(ct);

                // Runners antes dos runs: o alerta de "travado" precisa saber se há runner online.
                if (DateTimeOffset.UtcNow - _runnersAt > TimeSpan.FromSeconds(60))
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
                        Log.Warn($"Falha ao consultar {repo}: {ex.Message}");
                        _repoErrors[repo] = ex.Status == HttpStatusCode.NotFound
                            ? $"{repo}: não encontrado (sem acesso ou token sem escopo 'repo')"
                            : $"{repo}: {ex.Message}";
                    }
                    _nextPoll[repo] = DateTimeOffset.UtcNow.AddSeconds(anyActive ? _cfg.ActivePollSeconds : _cfg.IdlePollSeconds);
                }

                if (DateTimeOffset.UtcNow - _metricsAt > TimeSpan.FromSeconds(5))
                    await RefreshMetricsAsync(ct);

                if (DateTimeOffset.UtcNow - _pruneAt > TimeSpan.FromMinutes(15))
                {
                    _api.Prune(TimeSpan.FromMinutes(30));
                    _pruneAt = DateTimeOffset.UtcNow;
                }

                _error = null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (RateLimitException rl)
            {
                _error = $"Rate limit do GitHub esgotado. Volta às {rl.Reset.ToLocalTime():HH:mm}";
                Log.Warn(_error);
                delay = Clamp(rl.Reset - DateTimeOffset.UtcNow, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(15));
            }
            catch (GitHubException ex) when (ex.Status == HttpStatusCode.Unauthorized)
            {
                _error = "Token recusado pelo GitHub (expirado/revogado).";
                Log.Warn(_error);
                Publish();
                AuthFailed?.Invoke();
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                _error = "Sem conexão com api.github.com";
                Log.Warn(_error, ex);
                delay = TimeSpan.FromSeconds(15);
            }
            catch (Exception ex)
            {
                _error = "Erro: " + ex.Message;
                Log.Error("Erro no ciclo de monitoramento", ex);
                delay = TimeSpan.FromSeconds(15);
            }

            Publish();
            try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Força nova consulta de tudo no próximo ciclo.</summary>
    public void Kick()
    {
        _nextPoll.Clear();
        _runnersAt = DateTimeOffset.MinValue;
    }

    async Task RefreshReposAsync(CancellationToken ct)
    {
        if (_cfg.Repos.Count > 0)
        {
            _repos = _cfg.Repos.Where(r => r.Contains('/')).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        else
        {
            var list = await _api.GetAsync<List<Repo>>(
                $"user/repos?affiliation=owner,collaborator,organization_member&sort=pushed&direction=desc&per_page={_cfg.AutoRepoCount}", ct) ?? new();
            _repos = list.Where(r => !r.Archived).Select(r => r.FullName).ToList();
            Log.Info("Repos monitorados: " + string.Join(", ", _repos));
        }
        _reposAt = DateTimeOffset.UtcNow;

        foreach (var gone in _active.Keys.Except(_repos, StringComparer.OrdinalIgnoreCase).ToList())
            _active.Remove(gone);
    }

    async Task<bool> PollRepoAsync(string repo, CancellationToken ct)
    {
        var res = await _api.GetAsync<RunsResponse>($"repos/{repo}/actions/runs?per_page=30", ct);
        var runs = res?.WorkflowRuns ?? new();
        var activeRuns = runs.Where(r => !IsCompleted(r.Status) && Mine(r)).ToList();

        var prev = _active.GetValueOrDefault(repo) ?? new();
        var next = new Dictionary<long, RunView>();

        foreach (var run in activeRuns)
            next[run.Id] = await BuildRunViewAsync(repo, run, ct);

        // "Travado" de verdade = ninguém pegando. Se um runner self-hosted deste repo está ocupado
        // com outro job, a espera é fila normal (capacidade), não runner offline: sem alarme.
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
                var sh = v.Jobs.Any(j => j.SelfHosted) && !AnySelfHostedOnline(repo) ? " Nenhum runner self-hosted online." : "";
                Log.Warn($"Run travado na fila: {repo} · {v.Workflow} (#{v.Id})");
                Notify?.Invoke("Job parado na fila",
                    $"{Short(repo)} · {v.Workflow} está na fila há mais de {_cfg.StuckQueueMinutes} min.{sh}", NotifyKind.Warning);
            }
        }

        // Runs que sumiram da lista de ativos: terminaram (ou caíram para fora da página de 30).
        foreach (var (id, old) in prev)
        {
            if (next.ContainsKey(id)) continue;
            var run = runs.FirstOrDefault(r => r.Id == id);
            if (run == null)
            {
                try { run = await _api.GetAsync<WorkflowRun>($"repos/{repo}/actions/runs/{id}", ct); }
                catch (GitHubException ex) when (ex.Status == HttpStatusCode.NotFound) { run = null; }
            }

            if (run != null && !IsCompleted(run.Status))
                next[id] = await BuildRunViewAsync(repo, run, ct);
            else
                OnFinished(old, run);
        }

        _active[repo] = next;
        return next.Count > 0;
    }

    async Task<RunView> BuildRunViewAsync(string repo, WorkflowRun run, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var phase = run.Status.Equals("in_progress", StringComparison.OrdinalIgnoreCase) ? RunPhase.Running
            : run.Status.Equals("waiting", StringComparison.OrdinalIgnoreCase) ? RunPhase.Waiting
            : RunPhase.Queued;

        var jobs = new List<Job>();
        try { jobs = (await _api.GetAsync<JobsResponse>($"repos/{repo}/actions/runs/{run.Id}/jobs?per_page=100", ct))?.Jobs ?? new(); }
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
            bool stuck = queued && now - (j.CreatedAt ?? run.CreatedAt) > stuckLimit;
            jobViews.Add(new JobView
            {
                Name = j.Name,
                Status = j.Status,
                RunnerName = string.IsNullOrEmpty(j.RunnerName) ? null : j.RunnerName,
                SelfHosted = self,
                Label = j.Labels?.FirstOrDefault(l => !l.Equals("self-hosted", StringComparison.OrdinalIgnoreCase)),
                Stuck = stuck,
                Metrics = j.RunnerName != null && _runnerMetrics.TryGetValue(j.RunnerName, out var m) ? m : null,
            });
        }

        if (phase == RunPhase.Queued && jobViews.Any(j => j.Status == "in_progress"))
            phase = RunPhase.Running;

        bool runStuck = jobViews.Any(j => j.Stuck)
                        || (phase == RunPhase.Queued && jobViews.Count == 0 && now - run.CreatedAt > stuckLimit);

        // Deploy: pelo nome (regex) ou por um deployment criado para este commit depois que o run começou.
        bool isDeploy = _deployRx.IsMatch(run.Name) || _deployRx.IsMatch(run.DisplayTitle ?? "")
                        || jobs.Any(j => _deployRx.IsMatch(j.Name));
        if (!_deployEnvByRun.TryGetValue(run.Id, out var env))
        {
            try
            {
                var deps = await _api.GetAsync<List<Deployment>>($"repos/{repo}/deployments?sha={run.HeadSha}&per_page=5", ct);
                var d = deps?.FirstOrDefault(x => x.CreatedAt >= run.CreatedAt.AddMinutes(-1));
                if (d != null) _deployEnvByRun[run.Id] = env = d.Environment;
            }
            catch (GitHubException ex) when (ex is not RateLimitException && ex.Status != HttpStatusCode.Unauthorized) { }
        }
        if (env != null) isDeploy = true;

        double? eta = phase == RunPhase.Running ? await GetEtaAsync(repo, run.WorkflowId, ct) : null;

        var pr = run.PullRequests?.FirstOrDefault();
        var branch = pr != null
            ? $"{pr.Head.Ref} → {pr.Base.Ref}  (PR #{pr.Number})"
            : run.HeadBranch ?? "-";

        return new RunView
        {
            Id = run.Id,
            Repo = repo,
            Workflow = run.Name,
            Title = run.DisplayTitle,
            Branch = branch,
            Event = run.Event,
            Actor = run.TriggeringActor?.Login ?? run.Actor?.Login,
            Phase = phase,
            IsDeploy = isDeploy,
            DeployEnv = env,
            CreatedAt = run.CreatedAt,
            StartedAt = phase == RunPhase.Running
                ? (run.RunStartedAt ?? jobs.Where(j => j.StartedAt != null).Min(j => j.StartedAt) ?? run.CreatedAt)
                : null,
            EtaSeconds = eta,
            Url = run.HtmlUrl,
            Stuck = runStuck,
            Jobs = jobViews,
        };
    }

    async Task<double?> GetEtaAsync(string repo, long workflowId, CancellationToken ct)
    {
        if (_eta.TryGetValue(workflowId, out var c) && DateTimeOffset.UtcNow - c.At < TimeSpan.FromMinutes(30))
            return c.Median;
        double? median = null;
        try
        {
            var r = await _api.GetAsync<RunsResponse>($"repos/{repo}/actions/workflows/{workflowId}/runs?status=success&per_page=10", ct);
            var durations = (r?.WorkflowRuns ?? new())
                .Where(x => x.RunStartedAt != null)
                .Select(x => (x.UpdatedAt - x.RunStartedAt!.Value).TotalSeconds)
                .Where(s => s > 0 && s < 6 * 3600)
                .OrderBy(s => s).ToList();
            // Mediana: um run que ficou 2h travado não estraga a estimativa.
            if (durations.Count >= 2) median = durations[durations.Count / 2];
        }
        catch (GitHubException ex) when (ex is not RateLimitException && ex.Status != HttpStatusCode.Unauthorized) { }
        _eta[workflowId] = (median, DateTimeOffset.UtcNow);
        return median;
    }

    async Task RefreshRunnersAsync(CancellationToken ct)
    {
        foreach (var repo in _repos.Where(r => !_noRunnerAccess.Contains(r)))
        {
            try
            {
                var r = await _api.GetAsync<RunnersResponse>($"repos/{repo}/actions/runners?per_page=100", ct);
                var list = r?.Runners ?? new();
                if (list.Count > 0) _runners[repo] = list; else _runners.Remove(repo);
            }
            catch (GitHubException ex) when (ex.Status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound && ex is not RateLimitException)
            {
                // Listar runners exige admin no repo. Sem permissão: não insiste.
                if (_noRunnerAccess.Add(repo)) Log.Info($"Sem permissão para listar runners de {repo} (precisa de admin)");
            }
        }
        foreach (var gone in _runners.Keys.Except(_repos, StringComparer.OrdinalIgnoreCase).ToList())
            _runners.Remove(gone);
        _runnersAt = DateTimeOffset.UtcNow;
    }

    async Task RefreshMetricsAsync(CancellationToken ct)
    {
        _metricsAt = DateTimeOffset.UtcNow;
        var names = _runners.Values.SelectMany(x => x)
            .Where(r => r.Status.Equals("online", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Name)
            .Concat(_active.Values.SelectMany(d => d.Values).SelectMany(v => v.Jobs)
                .Where(j => j.SelfHosted && j.RunnerName != null).Select(j => j.RunnerName!))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var results = await Task.WhenAll(names.Select(async n => (n, m: await _metrics.GetAsync(n, ct))));
        _runnerMetrics.Clear();
        foreach (var (n, m) in results)
            if (m != null) _runnerMetrics[n] = m;
    }

    void OnFinished(RunView old, WorkflowRun? run)
    {
        _stuckNotified.Remove(old.Id);
        _deployEnvByRun.Remove(old.Id);
        if (!_cfg.Notifications || run == null) return;

        var (verb, kind) = run.Conclusion switch
        {
            "success" => ("concluído ✔", NotifyKind.Success),
            "failure" or "timed_out" or "startup_failure" => ("FALHOU ✖", NotifyKind.Failure),
            "cancelled" => ("cancelado", NotifyKind.Info),
            _ => (run.Conclusion ?? "finalizado", NotifyKind.Info),
        };
        // Só notifica sucesso de deploy (sucesso de CI comum vira ruído); falha notifica sempre.
        if (kind == NotifyKind.Success && !old.IsDeploy) return;
        if (kind == NotifyKind.Info && !old.IsDeploy) return;

        var dur = run.RunStartedAt is { } s ? Fmt(run.UpdatedAt - s) : "";
        var what = old.IsDeploy ? $"Deploy{(old.DeployEnv != null ? " " + old.DeployEnv : "")}" : old.Workflow;
        Notify?.Invoke($"{what} {verb}", $"{Short(old.Repo)} · {old.Branch}  {dur}".Trim(), kind);
    }

    void Publish()
    {
        var runs = _active.Values.SelectMany(d => d.Values)
            .Select(WithFreshMetrics)
            .OrderBy(r => r.Phase == RunPhase.Running ? 0 : 1)
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
            .GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase) // mesma máquina registrada em vários repos
            .Select(g => g.OrderByDescending(r => r.Busy).First())
            .OrderBy(r => r.Status == "online" ? 0 : 1).ThenBy(r => r.Name)
            .ToList();

        Updated?.Invoke(new Snapshot
        {
            Runs = runs,
            Runners = runners,
            RepoCount = _repos.Count,
            RateRemaining = _api.RateRemaining,
            Error = _error,
            RepoErrors = _repoErrors.Values.ToList(),
        });
    }

    // Métricas mudam a cada 5s, jobs só quando o GitHub muda: junta as duas na hora de publicar.
    RunView WithFreshMetrics(RunView r)
    {
        if (r.Jobs.Count == 0 || _runnerMetrics.Count == 0) return r;
        var jobs = r.Jobs.Select(j => j.RunnerName != null && _runnerMetrics.TryGetValue(j.RunnerName, out var m)
            ? new JobView { Name = j.Name, Status = j.Status, RunnerName = j.RunnerName, SelfHosted = j.SelfHosted, Label = j.Label, Stuck = j.Stuck, Metrics = m }
            : j).ToList();
        return new RunView
        {
            Id = r.Id, Repo = r.Repo, Workflow = r.Workflow, Title = r.Title, Branch = r.Branch, Event = r.Event,
            Actor = r.Actor, Phase = r.Phase, IsDeploy = r.IsDeploy, DeployEnv = r.DeployEnv, CreatedAt = r.CreatedAt,
            StartedAt = r.StartedAt, EtaSeconds = r.EtaSeconds, Url = r.Url, Stuck = r.Stuck, RunnerBusy = r.RunnerBusy, Jobs = jobs,
        };
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
