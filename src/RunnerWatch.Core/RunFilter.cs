namespace RunnerWatch.Core;

/// <summary>
/// Repos and workflows the user chose to hide (right-click a run). The lists in <see cref="AppConfig"/> are replaced,
/// never mutated, so the polling thread can read them while the UI thread changes them.
/// </summary>
public static class RunFilter
{
    public static string WorkflowKey(string repo, string workflow) => $"{repo}:{workflow}";

    public static bool RepoHidden(AppConfig cfg, string repo) =>
        cfg.HiddenRepos.Contains(repo, StringComparer.OrdinalIgnoreCase);

    public static bool Visible(AppConfig cfg, string repo, string workflow) =>
        !RepoHidden(cfg, repo) && !cfg.HiddenWorkflows.Contains(WorkflowKey(repo, workflow), StringComparer.OrdinalIgnoreCase);

    public static int HiddenCount(AppConfig cfg) => cfg.HiddenRepos.Count + cfg.HiddenWorkflows.Count;
}
