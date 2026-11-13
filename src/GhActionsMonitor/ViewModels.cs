using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace GhActionsMonitor;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public static class Palette
{
    static SolidColorBrush B(string hex)
    {
        var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        b.Freeze();
        return b;
    }
    public static readonly SolidColorBrush Running = B("#3B9EFF");
    public static readonly SolidColorBrush Deploy = B("#FF9F1C");
    public static readonly SolidColorBrush Queued = B("#E3B341");
    public static readonly SolidColorBrush Waiting = B("#A371F7");
    public static readonly SolidColorBrush Stuck = B("#F85149");
    public static readonly SolidColorBrush Idle = B("#6E7681");
    public static readonly SolidColorBrush Ok = B("#3FB950");
}

public sealed class RunVm : Observable
{
    RunView _v = null!;
    public long Id { get; }
    public RunVm(RunView v) { Id = v.Id; Update(v); }

    string _title = "", _sub = "", _jobs = "", _time = "", _badge = "", _url = "";
    Brush _brush = Palette.Running, _badgeBrush = Palette.Deploy;
    double _progress;
    Visibility _progressVis = Visibility.Collapsed, _jobsVis = Visibility.Collapsed, _badgeVis = Visibility.Collapsed;

    public string Title { get => _title; private set => Set(ref _title, value); }
    public string Sub { get => _sub; private set => Set(ref _sub, value); }
    public string JobsText { get => _jobs; private set => Set(ref _jobs, value); }
    public string TimeText { get => _time; private set => Set(ref _time, value); }
    public string Badge { get => _badge; private set => Set(ref _badge, value); }
    public string Url { get => _url; private set => Set(ref _url, value); }
    public Brush StatusBrush { get => _brush; private set => Set(ref _brush, value); }
    public Brush BadgeBrush { get => _badgeBrush; private set => Set(ref _badgeBrush, value); }
    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public Visibility ProgressVisibility { get => _progressVis; private set => Set(ref _progressVis, value); }
    public Visibility JobsVisibility { get => _jobsVis; private set => Set(ref _jobsVis, value); }
    public Visibility BadgeVisibility { get => _badgeVis; private set => Set(ref _badgeVis, value); }
    public RunPhase Phase => _v.Phase;

    public void Update(RunView v)
    {
        _v = v;
        var repo = v.Repo[(v.Repo.IndexOf('/') + 1)..];
        Title = $"{v.Workflow} · {repo}";
        Sub = $"{v.Branch} · {v.Event}{(v.Actor != null ? " · " + v.Actor : "")}";
        Url = v.Url;

        if (v.Stuck) { Badge = "TRAVADO"; BadgeBrush = Palette.Stuck; }
        else if (v.Phase == RunPhase.Waiting) { Badge = "APROVAÇÃO"; BadgeBrush = Palette.Waiting; }
        else if (v.IsDeploy) { Badge = v.DeployEnv != null ? $"DEPLOY {v.DeployEnv}".ToUpperInvariant() : "DEPLOY"; BadgeBrush = Palette.Deploy; }
        else Badge = "";
        BadgeVisibility = Badge.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        StatusBrush = v.Stuck ? Palette.Stuck
            : v.Phase == RunPhase.Waiting ? Palette.Waiting
            : v.Phase == RunPhase.Queued ? Palette.Queued
            : v.IsDeploy ? Palette.Deploy : Palette.Running;

        var lines = v.Jobs.Select(j =>
        {
            var where = j.RunnerName != null
                ? (j.SelfHosted ? $"self:{j.RunnerName}" : $"hosted:{j.Label ?? j.RunnerName}")
                : (j.SelfHosted ? "aguardando self-hosted" : $"aguardando {j.Label ?? "runner"}");
            var m = j.Metrics;
            var res = m == null ? "" : $"  CPU {m.CpuPercent?.ToString("0") ?? "?"}%  RAM {Gb(m.MemUsed)}/{Gb(m.MemTotal)} GB";
            var icon = j.Stuck ? "⚠" : j.Status == "in_progress" ? "▶" : "⏳";
            return $"{icon} {j.Name}  [{where}]{res}";
        }).Take(4).ToList();
        if (v.Jobs.Count > 4) lines.Add($"+{v.Jobs.Count - 4} jobs");
        JobsText = string.Join("\n", lines);
        JobsVisibility = lines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        Tick(DateTimeOffset.UtcNow);
    }

    public void Tick(DateTimeOffset now)
    {
        if (_v.Phase == RunPhase.Running && _v.StartedAt is { } s)
        {
            var el = now - s;
            if (_v.EtaSeconds is { } eta && eta > 0)
            {
                Progress = Math.Min(1, el.TotalSeconds / eta);
                var left = TimeSpan.FromSeconds(eta) - el;
                TimeText = left > TimeSpan.Zero
                    ? $"{ActionsMonitor.Fmt(el)} · ~{ActionsMonitor.Fmt(left)}"
                    : $"{ActionsMonitor.Fmt(el)} · atrasado";
                ProgressVisibility = Visibility.Visible;
            }
            else
            {
                TimeText = ActionsMonitor.Fmt(el);
                ProgressVisibility = Visibility.Collapsed;
            }
        }
        else
        {
            TimeText = "fila " + ActionsMonitor.Fmt(now - _v.CreatedAt);
            ProgressVisibility = Visibility.Collapsed;
        }
    }

    static string Gb(long? b) => b is { } v ? (v / 1073741824.0).ToString("0.0") : "?";
}

public sealed class RunnerVm : Observable
{
    public string Name { get; }
    public RunnerVm(RunnerView v) { Name = v.Name; Update(v); }

    string _detail = "", _cpuText = "", _ramText = "";
    double _cpu, _ram;
    Brush _brush = Palette.Idle;
    Visibility _metricsVis = Visibility.Collapsed;

    public string Detail { get => _detail; private set => Set(ref _detail, value); }
    public string CpuText { get => _cpuText; private set => Set(ref _cpuText, value); }
    public string RamText { get => _ramText; private set => Set(ref _ramText, value); }
    public double Cpu { get => _cpu; private set => Set(ref _cpu, value); }
    public double Ram { get => _ram; private set => Set(ref _ram, value); }
    public Brush StatusBrush { get => _brush; private set => Set(ref _brush, value); }
    public Visibility MetricsVisibility { get => _metricsVis; private set => Set(ref _metricsVis, value); }

    public void Update(RunnerView v)
    {
        bool online = v.Status.Equals("online", StringComparison.OrdinalIgnoreCase);
        StatusBrush = !online ? Palette.Stuck : v.Busy ? Palette.Running : Palette.Ok;
        Detail = !online ? "offline" : v.Busy ? "ocupado" : "livre";
        var m = v.Metrics;
        if (m != null)
        {
            Cpu = (m.CpuPercent ?? 0) / 100.0;
            CpuText = m.CpuPercent is { } c ? $"CPU {c:0}%" : "CPU ?";
            Ram = m.MemTotal is > 0 && m.MemUsed is { } u ? (double)u / m.MemTotal.Value : 0;
            RamText = m.MemTotal is > 0 ? $"RAM {m.MemUsed / 1073741824.0:0.0}/{m.MemTotal / 1073741824.0:0.0} GB" : "RAM ?";
            MetricsVisibility = Visibility.Visible;
        }
        else
        {
            CpuText = RamText = "";
            MetricsVisibility = Visibility.Collapsed;
            if (online) Detail += " · sem agente";
        }
    }
}

public sealed class MainVm : Observable
{
    public ObservableCollection<RunVm> Runs { get; } = new();
    public ObservableCollection<RunnerVm> Runners { get; } = new();

    string _header = "Actions", _footer = "Conectando…";
    Brush _headerBrush = Palette.Idle, _footerBrush = Palette.Idle;
    Visibility _runnersVis = Visibility.Collapsed;

    public string Header { get => _header; set => Set(ref _header, value); }
    public string Footer { get => _footer; set => Set(ref _footer, value); }
    public Brush HeaderBrush { get => _headerBrush; set => Set(ref _headerBrush, value); }
    public Brush FooterBrush { get => _footerBrush; set => Set(ref _footerBrush, value); }
    public Visibility RunnersVisibility { get => _runnersVis; set => Set(ref _runnersVis, value); }

    public int RunningCount { get; private set; }
    public int QueuedCount { get; private set; }
    public bool AnyStuck { get; private set; }
    public bool AnyDeploy { get; private set; }

    public void Apply(Snapshot s)
    {
        Sync(Runs, s.Runs, v => v.Id, vm => vm.Id, v => new RunVm(v), (vm, v) => vm.Update(v));
        Sync(Runners, s.Runners, v => v.Name, vm => vm.Name, v => new RunnerVm(v), (vm, v) => vm.Update(v));
        RunnersVisibility = Runners.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        RunningCount = s.Runs.Count(r => r.Phase == RunPhase.Running);
        // Fila = runs inteiros na fila + jobs na fila dentro de runs que já começaram.
        QueuedCount = s.Runs.Count(r => r.Phase != RunPhase.Running)
                      + s.Runs.Where(r => r.Phase == RunPhase.Running).Sum(r => r.Jobs.Count(j => j.Status != "in_progress"));
        AnyStuck = s.Runs.Any(r => r.Stuck);
        AnyDeploy = s.Runs.Any(r => r.IsDeploy && r.Phase == RunPhase.Running);

        Header = RunningCount + QueuedCount == 0 ? "Actions · ocioso" : $"Actions   ▶ {RunningCount}   ⏳ {QueuedCount}";
        HeaderBrush = s.Error != null || AnyStuck ? Palette.Stuck
            : AnyDeploy ? Palette.Deploy
            : RunningCount > 0 ? Palette.Running
            : QueuedCount > 0 ? Palette.Queued : Palette.Idle;

        if (s.Error != null) { Footer = s.Error; FooterBrush = Palette.Stuck; }
        else if (s.RepoErrors.Count > 0) { Footer = s.RepoErrors[0] + (s.RepoErrors.Count > 1 ? $" (+{s.RepoErrors.Count - 1})" : ""); FooterBrush = Palette.Queued; }
        else
        {
            Footer = $"{s.RepoCount} repos · API {(s.RateRemaining >= 0 ? s.RateRemaining.ToString() : "?")} · {s.At.ToLocalTime():HH:mm:ss}";
            FooterBrush = Palette.Idle;
        }
    }

    public void Tick()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var r in Runs) r.Tick(now);
    }

    static void Sync<TView, TVm, TKey>(ObservableCollection<TVm> col, System.Collections.Generic.List<TView> views,
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
            if (idx < 0) col.Insert(i, create(views[i]));
            else
            {
                update(col[idx], views[i]);
                if (idx != i) col.Move(idx, i);
            }
        }
    }
}
