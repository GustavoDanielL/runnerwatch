using System.Text.Json.Serialization;

namespace RunnerWatch.Core;

// ---- GitHub API DTOs (snake_case) ----
public sealed record GhUser(string Login);
public sealed record BranchRef(string Ref);
public sealed record PullRequestRef(long Number, BranchRef Head, BranchRef Base);

public sealed record WorkflowRun(
    long Id, string Name, string? DisplayTitle, string Status, string? Conclusion, string Event,
    string? HeadBranch, string HeadSha, long WorkflowId, DateTimeOffset CreatedAt,
    DateTimeOffset? RunStartedAt, DateTimeOffset UpdatedAt, string HtmlUrl, int RunAttempt,
    List<PullRequestRef>? PullRequests, GhUser? Actor, GhUser? TriggeringActor);

public sealed record RunsResponse(int TotalCount, List<WorkflowRun>? WorkflowRuns);

public sealed record Job(
    long Id, string Name, string Status, string? Conclusion, DateTimeOffset? CreatedAt,
    DateTimeOffset? StartedAt, string? RunnerName, List<string>? Labels, string HtmlUrl);

public sealed record JobsResponse(int TotalCount, List<Job>? Jobs);

public sealed record RunnerLabel(string Name);
public sealed record Runner(long Id, string Name, string Os, string Status, bool Busy, List<RunnerLabel>? Labels);
public sealed record RunnersResponse(int TotalCount, List<Runner>? Runners);

public sealed record Deployment(long Id, string Environment, DateTimeOffset CreatedAt);
public sealed record Repo(string FullName, DateTimeOffset? PushedAt, bool Archived);

// ---- Runner metrics (agent / local machine) ----
public sealed record AgentMetrics(double? CpuPercent, long? MemUsed, long? MemTotal, string? Hostname);

/// <summary>Source-generated JSON for the GitHub API and the metrics agent (Native AOT: no reflection).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(GhUser))]
[JsonSerializable(typeof(WorkflowRun))]
[JsonSerializable(typeof(RunsResponse))]
[JsonSerializable(typeof(JobsResponse))]
[JsonSerializable(typeof(RunnersResponse))]
[JsonSerializable(typeof(List<Deployment>))]
[JsonSerializable(typeof(List<Repo>))]
[JsonSerializable(typeof(AgentMetrics))]
public sealed partial class GitHubJson : JsonSerializerContext;

// ---- Snapshot handed to the UI ----
public enum RunPhase { Running, Queued, Waiting, Failed }

public sealed class JobView
{
    public required string Name { get; init; }
    public required string Status { get; init; }
    public string? RunnerName { get; init; }
    public bool SelfHosted { get; init; }
    public string? Label { get; init; }
    public bool Stuck { get; init; }
    public AgentMetrics? Metrics { get; init; }
}

public sealed class RunView
{
    public required long Id { get; init; }
    public required string Repo { get; init; }
    public required string Workflow { get; init; }
    public string? Title { get; init; }
    public required string Branch { get; init; }
    public required string Event { get; init; }
    public string? Actor { get; init; }
    public required RunPhase Phase { get; set; }
    public bool IsDeploy { get; init; }
    public string? DeployEnv { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; set; }
    public double? EtaSeconds { get; init; }
    public required string Url { get; init; }
    public bool Stuck { get; set; }
    /// <summary>Queued only because the self-hosted runner is busy with another job (capacity, not an outage).</summary>
    public bool RunnerBusy { get; set; }
    public List<JobView> Jobs { get; init; } = new();

    public RunView With(List<JobView> jobs) => new()
    {
        Id = Id, Repo = Repo, Workflow = Workflow, Title = Title, Branch = Branch, Event = Event, Actor = Actor,
        Phase = Phase, IsDeploy = IsDeploy, DeployEnv = DeployEnv, CreatedAt = CreatedAt, StartedAt = StartedAt,
        FinishedAt = FinishedAt, EtaSeconds = EtaSeconds, Url = Url, Stuck = Stuck, RunnerBusy = RunnerBusy, Jobs = jobs,
    };
}

public sealed class RunnerView
{
    public required string Name { get; init; }
    public required string Repo { get; init; }
    public required string Status { get; init; }
    public bool Busy { get; init; }
    public string? Os { get; init; }
    public AgentMetrics? Metrics { get; init; }
}

public sealed class Snapshot
{
    public List<RunView> Runs { get; init; } = new();
    public List<RunnerView> Runners { get; init; } = new();
    public int RepoCount { get; init; }
    public int RateRemaining { get; init; }
    public string? Error { get; init; }
    public List<string> RepoErrors { get; init; } = new();
    public DateTimeOffset At { get; init; } = DateTimeOffset.Now;
}

public enum NotifyKind { Info, Success, Failure, Warning }

/// <summary>Anything that produces snapshots for the widget: the real GitHub monitor or the demo.</summary>
public interface ISnapshotSource
{
    event Action<Snapshot>? Updated;
    event Action<string, string, NotifyKind>? Notify;
    event Action? AuthFailed;
    Task RunAsync(CancellationToken ct);
    void Kick();
    /// <summary>Re-runs only the failed jobs of a run. Returns an error message, or null on success.</summary>
    Task<string?> RerunFailedAsync(RunView run, CancellationToken ct);
}
