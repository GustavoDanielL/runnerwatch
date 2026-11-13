using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RunnerWatch.Core;

namespace RunnerWatch;

public static class Palette
{
    public static readonly IImmutableSolidColorBrush Running = new ImmutableSolidColorBrush(Color.Parse("#3B9EFF"));
    public static readonly IImmutableSolidColorBrush Deploy = new ImmutableSolidColorBrush(Color.Parse("#FF9F1C"));
    public static readonly IImmutableSolidColorBrush Queued = new ImmutableSolidColorBrush(Color.Parse("#E3B341"));
    public static readonly IImmutableSolidColorBrush Waiting = new ImmutableSolidColorBrush(Color.Parse("#A371F7"));
    public static readonly IImmutableSolidColorBrush Stuck = new ImmutableSolidColorBrush(Color.Parse("#F85149"));
    public static readonly IImmutableSolidColorBrush Idle = new ImmutableSolidColorBrush(Color.Parse("#6E7681"));
    public static readonly IImmutableSolidColorBrush Ok = new ImmutableSolidColorBrush(Color.Parse("#3FB950"));
}

/// <summary>What a run item can ask the app to do (open in browser, re-run).</summary>
public interface IRunActions
{
    void Open(string url);
    Task RerunFailedAsync(RunView run);
}

public sealed partial class RunVm : ObservableObject
{
    readonly IRunActions _actions;
    RunView _v = null!;
    public long Id { get; }

    [ObservableProperty] public partial string Title { get; set; }
    [ObservableProperty] public partial string Sub { get; set; }
    [ObservableProperty] public partial string JobsText { get; set; }
    [ObservableProperty] public partial string TimeText { get; set; }
    [ObservableProperty] public partial string Badge { get; set; }
    [ObservableProperty] public partial IBrush StatusBrush { get; set; }
    [ObservableProperty] public partial IBrush BadgeBrush { get; set; }
    [ObservableProperty] public partial double Progress { get; set; }
    [ObservableProperty] public partial bool ProgressVisible { get; set; }
    [ObservableProperty] public partial bool JobsVisible { get; set; }
    [ObservableProperty] public partial bool BadgeVisible { get; set; }
    [ObservableProperty] public partial bool CanRerun { get; set; }

    public string OpenText => L.T("run.open");
    public string RerunText => L.T("run.rerunFailed");
    public string Tooltip => L.T("run.tooltip");

    public RunVm(RunView v, IRunActions actions)
    {
        _actions = actions;
        Id = v.Id;
        Title = Sub = JobsText = TimeText = Badge = "";
        StatusBrush = Palette.Running;
        BadgeBrush = Palette.Deploy;
        Update(v);
    }

    [RelayCommand] void Open() => _actions.Open(_v.Url);

    [RelayCommand(CanExecute = nameof(CanRerun))]
    Task Rerun() => _actions.RerunFailedAsync(_v);

    partial void OnCanRerunChanged(bool value) => RerunCommand.NotifyCanExecuteChanged();

    public void Update(RunView v)
    {
        _v = v;
        var repo = v.Repo[(v.Repo.IndexOf('/') + 1)..];
        Title = $"{v.Workflow} · {repo}";
        Sub = $"{v.Branch} · {v.Event}{(v.Actor != null ? " · " + v.Actor : "")}";
        CanRerun = v.Phase == RunPhase.Failed;

        (Badge, BadgeBrush) = v switch
        {
            { Phase: RunPhase.Failed } => (L.T("badge.failed"), Palette.Stuck),
            { Stuck: true } => (L.T("badge.stuck"), Palette.Stuck),
            { RunnerBusy: true } => (L.T("badge.busy"), Palette.Queued),
            { Phase: RunPhase.Waiting } => (L.T("badge.approval"), Palette.Waiting),
            { IsDeploy: true } => (v.DeployEnv != null ? $"{L.T("badge.deploy")} {v.DeployEnv}".ToUpperInvariant() : L.T("badge.deploy"), (IBrush)Palette.Deploy),
            _ => ("", BadgeBrush),
        };
        BadgeVisible = Badge.Length > 0;

        StatusBrush = v.Phase == RunPhase.Failed || v.Stuck ? Palette.Stuck
            : v.Phase == RunPhase.Waiting ? Palette.Waiting
            : v.Phase == RunPhase.Queued ? Palette.Queued
            : v.IsDeploy ? Palette.Deploy : Palette.Running;

        var lines = v.Jobs.Select(j =>
        {
            var where = j.RunnerName != null
                ? (j.SelfHosted ? $"self:{j.RunnerName}" : $"hosted:{j.Label ?? j.RunnerName}")
                : (j.SelfHosted ? L.T("job.waitingSelf") : L.F("job.waiting", j.Label ?? L.T("job.runner")));
            var m = j.Metrics;
            var res = m == null ? "" : $"  CPU {m.CpuPercent?.ToString("0", L.Culture) ?? "?"}%  RAM {Gb(m.MemUsed)}/{Gb(m.MemTotal)} GB";
            var icon = j.Stuck && !v.RunnerBusy ? "⚠" : j.Status == "in_progress" ? "▶" : "⏳";
            return $"{icon} {j.Name}  [{where}]{res}";
        }).Take(4).ToList();
        if (v.Jobs.Count > 4) lines.Add(L.F("jobs.more", v.Jobs.Count - 4));
        JobsText = string.Join("\n", lines);
        JobsVisible = lines.Count > 0;

        Tick(DateTimeOffset.UtcNow);
    }

    public void Tick(DateTimeOffset now)
    {
        if (_v.Phase == RunPhase.Failed)
        {
            TimeText = L.F("time.failed", ActionsMonitor.Fmt(now - (_v.FinishedAt ?? now)));
            ProgressVisible = false;
        }
        else if (_v.Phase == RunPhase.Running && _v.StartedAt is { } s)
        {
            var el = now - s;
            if (_v.EtaSeconds is { } eta && eta > 0)
            {
                Progress = Math.Min(1, el.TotalSeconds / eta);
                var left = TimeSpan.FromSeconds(eta) - el;
                TimeText = left > TimeSpan.Zero
                    ? $"{ActionsMonitor.Fmt(el)} · ~{ActionsMonitor.Fmt(left)}"
                    : $"{ActionsMonitor.Fmt(el)} · {L.T("time.late")}";
                ProgressVisible = true;
            }
            else
            {
                TimeText = ActionsMonitor.Fmt(el);
                ProgressVisible = false;
            }
        }
        else
        {
            TimeText = L.F("time.queued", ActionsMonitor.Fmt(now - _v.CreatedAt));
            ProgressVisible = false;
        }
    }

    static string Gb(long? b) => b is { } v ? (v / 1073741824.0).ToString("0.0", L.Culture) : "?";
}

public sealed partial class RunnerVm : ObservableObject
{
    public string Name { get; }

    [ObservableProperty] public partial string Detail { get; set; }
    [ObservableProperty] public partial string CpuText { get; set; }
    [ObservableProperty] public partial string RamText { get; set; }
    [ObservableProperty] public partial double Cpu { get; set; }
    [ObservableProperty] public partial double Ram { get; set; }
    [ObservableProperty] public partial IBrush StatusBrush { get; set; }
    [ObservableProperty] public partial bool MetricsVisible { get; set; }

    public RunnerVm(RunnerView v)
    {
        Name = v.Name;
        Detail = CpuText = RamText = "";
        StatusBrush = Palette.Idle;
        Update(v);
    }

    public void Update(RunnerView v)
    {
        bool online = v.Status.Equals("online", StringComparison.OrdinalIgnoreCase);
        StatusBrush = !online ? Palette.Stuck : v.Busy ? Palette.Running : Palette.Ok;
        var detail = !online ? L.T("runner.offline") : v.Busy ? L.T("runner.busy") : L.T("runner.idle");
        var m = v.Metrics;
        if (m != null)
        {
            Cpu = (m.CpuPercent ?? 0) / 100.0;
            CpuText = m.CpuPercent is { } c ? string.Create(L.Culture, $"CPU {c:0}%") : "CPU ?";
            Ram = m.MemTotal is > 0 && m.MemUsed is { } u ? (double)u / m.MemTotal.Value : 0;
            RamText = m.MemTotal is > 0 ? string.Create(L.Culture, $"RAM {m.MemUsed / 1073741824.0:0.0}/{m.MemTotal / 1073741824.0:0.0} GB") : "RAM ?";
            MetricsVisible = true;
        }
        else
        {
            CpuText = RamText = "";
            MetricsVisible = false;
            if (online) detail += L.T("runner.noAgent");
        }
        Detail = detail;
    }
}

public sealed partial class MainVm : ObservableObject
{
    readonly IRunActions _actions;
    public ObservableCollection<RunVm> Runs { get; } = new();
    public ObservableCollection<RunnerVm> Runners { get; } = new();

    [ObservableProperty] public partial string Header { get; set; }
    [ObservableProperty] public partial string Footer { get; set; }
    [ObservableProperty] public partial IBrush HeaderBrush { get; set; }
    [ObservableProperty] public partial IBrush FooterBrush { get; set; }
    [ObservableProperty] public partial bool RunnersVisible { get; set; }

    public string RunnersTitle => L.T("runners.title");
    public string RefreshTooltip => L.T("refresh.tooltip");
    public bool IsDemo { get; init; }

    public int RunningCount { get; private set; }
    public int QueuedCount { get; private set; }
    public bool AnyStuck { get; private set; }
    public bool AnyDeploy { get; private set; }
    public bool HasActiveRuns => RunningCount + QueuedCount > 0;

    public MainVm(IRunActions actions)
    {
        _actions = actions;
        Header = "Actions";
        Footer = L.T("status.connecting");
        HeaderBrush = FooterBrush = Palette.Idle;
    }

    public void Apply(Snapshot s)
    {
        Sync(Runs, s.Runs, v => v.Id, vm => vm.Id, v => new RunVm(v, _actions), (vm, v) => vm.Update(v));
        Sync(Runners, s.Runners, v => v.Name, vm => vm.Name, v => new RunnerVm(v), (vm, v) => vm.Update(v));
        RunnersVisible = Runners.Count > 0;

        var active = s.Runs.Where(r => r.Phase != RunPhase.Failed).ToList();
        RunningCount = active.Count(r => r.Phase == RunPhase.Running);
        // Queue = whole runs waiting + queued jobs inside runs that already started.
        QueuedCount = active.Count(r => r.Phase != RunPhase.Running)
                      + active.Where(r => r.Phase == RunPhase.Running).Sum(r => r.Jobs.Count(j => j.Status != "in_progress"));
        AnyStuck = active.Any(r => r.Stuck);
        AnyDeploy = active.Any(r => r.IsDeploy && r.Phase == RunPhase.Running);

        Header = RunningCount + QueuedCount == 0 ? L.T("header.idle") : L.F("header.counts", RunningCount, QueuedCount);
        HeaderBrush = s.Error != null || AnyStuck ? Palette.Stuck
            : AnyDeploy ? Palette.Deploy
            : RunningCount > 0 ? Palette.Running
            : QueuedCount > 0 ? Palette.Queued : Palette.Idle;

        if (s.Error != null) { Footer = s.Error; FooterBrush = Palette.Stuck; }
        else if (s.RepoErrors.Count > 0) { Footer = s.RepoErrors[0] + (s.RepoErrors.Count > 1 ? $" (+{s.RepoErrors.Count - 1})" : ""); FooterBrush = Palette.Queued; }
        else
        {
            var at = s.At.ToLocalTime().ToString("HH:mm:ss");
            Footer = IsDemo ? L.F("footer.demo", at)
                : L.F("footer.status", s.RepoCount, s.RateRemaining >= 0 ? s.RateRemaining.ToString() : "?", at);
            FooterBrush = Palette.Idle;
        }
    }

    public void Tick()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var r in Runs) r.Tick(now);
    }

    /// <summary>Minimal in-place sync so items keep their visual state (no full re-render every cycle).</summary>
    static void Sync<TView, TVm, TKey>(ObservableCollection<TVm> col, List<TView> views,
        Func<TView, TKey> vKey, Func<TVm, TKey> vmKey, Func<TView, TVm> create, Action<TVm, TView> update) where TKey : notnull
    {
        var keys = views.Select(vKey).ToHashSet();
        for (int i = col.Count - 1; i >= 0; i--)
            if (!keys.Contains(vmKey(col[i]))) col.RemoveAt(i);

        for (int i = 0; i < views.Count; i++)
        {
            var key = vKey(views[i]);
            int idx = -1;
            for (int k = 0; k < col.Count; k++) if (Equals(vmKey(col[k]), key)) { idx = k; break; }
            if (idx < 0) col.Insert(Math.Min(i, col.Count), create(views[i]));
            else
            {
                update(col[idx], views[i]);
                if (idx != i) col.Move(idx, i);
            }
        }
    }
}
