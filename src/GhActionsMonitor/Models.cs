using System;
using System.Collections.Generic;

namespace GhActionsMonitor;

// ---- DTOs da API do GitHub (snake_case) ----
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

// ---- Métricas (agente / local) ----
public sealed record AgentMetrics(double? CpuPercent, long? MemUsed, long? MemTotal, string? Hostname);

// ---- Snapshot entregue para a UI ----
public enum RunPhase { Running, Queued, Waiting }

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
    public required RunPhase Phase { get; init; }
    public bool IsDeploy { get; init; }
    public string? DeployEnv { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public double? EtaSeconds { get; init; }
    public required string Url { get; init; }
    public bool Stuck { get; set; }
    /// <summary>Na fila porque o runner self-hosted está ocupado com outro job (não é travamento).</summary>
    public bool RunnerBusy { get; set; }
    public List<JobView> Jobs { get; init; } = new();
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
