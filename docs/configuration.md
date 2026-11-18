# Configuration

The settings file is created on first run:

| OS | Path |
|---|---|
| Windows | `%APPDATA%\RunnerWatch\config.json` |
| macOS | `~/Library/Application Support/RunnerWatch/config.json` |
| Linux | `~/.config/RunnerWatch/config.json` |

`--demo` uses a separate `config.demo.json`, so trying the demo never changes these settings.

Open it from the tray icon (**Open settings…**), edit, then choose **Apply settings (restart)**. Comments and trailing commas are allowed. If the file is invalid, RunnerWatch keeps a copy as `config.json.broken` and starts with the defaults.

## Options

| Key | Default | Description |
|---|---|---|
| `repos` | `[]` | `"owner/repo"` list. Empty = your `autoRepoCount` most recently pushed repos (own, organization and collaborator). |
| `autoRepoCount` | `10` | How many repos auto-discovery watches (1–50). |
| `apiBaseUrl` | `https://api.github.com/` | GitHub Enterprise Server: `https://HOST/api/v3/`. |
| `activePollSeconds` | `10` | Poll interval for a repo with something running (min 5). |
| `idlePollSeconds` | `60` | Poll interval for an idle repo. |
| `deployPattern` | `deploy\|release\|publish\|prod` | Case-insensitive regex on workflow/job/title names that marks a run as a deploy. A deployment created for the commit also counts. |
| `stuckQueueMinutes` | `5` | Queue time after which a job nobody picks up is **STUCK** (and notifies). |
| `showFailuresMinutes` | `15` | How long failed runs stay visible with a re-run option. `0` hides them. |
| `notifications` | `true` | Desktop notifications (stuck jobs, failures, finished deploys, runner alerts). |
| `paidRunnerAlert` | `auto` | Flag jobs on GitHub-hosted (billed) runners as **PAID RUNNER** and notify. `auto`: private repos known to use self-hosted runners, meaning runners are registered in the repo or a self-hosted job was seen there (remembered in `self-hosted-repos.txt` next to the config). `all`: every private repo. `off` disables. Public repos are never flagged. Recognises the standard `ubuntu-*`, `windows-*` and `macos-*` labels and the "GitHub Actions" runner group; larger runners with custom labels are not detected. |
| `runnerOfflineMinutes` | `3` | Notify when a self-hosted runner that was online in this session stays offline this long, and again when it comes back. `0` disables. Needs permission to list runners (repo admin). |
| `runnerMemoryAlertPercent` | `90` | Notify when a runner's RAM stays at or above this percentage for 2 minutes; the RAM bar turns red. Needs metrics (same machine or agent). `0` disables. |
| `onlyMine` | `false` | Only show runs triggered by you. Also in tray → Filters. |
| `hiddenRepos` | `[]` | `"owner/repo"` entries hidden from the widget and not polled. Right-click a run → Hide repository; tray → Filters → Show hidden clears the list. |
| `hiddenWorkflows` | `[]` | `"owner/repo:Workflow name"` entries hidden from the widget (no notifications either). Right-click a run → Hide workflow. |
| `agentUrlTemplate` | `http://{runner}:9465` | Where to find the metrics agent of a self-hosted runner. `""` disables. |
| `agents` | `{}` | Per-runner URL override: `{ "runner-name": "http://10.0.0.5:9465" }`. |
| `agentToken` | `null` | Sent as `X-Agent-Token` if the agents use `--token`. |
| `language` | `auto` | `auto` follows the OS, or a code such as `en` / `pt-BR` / `es`. |
| `theme` | `system` | `system`, `dark` or `light`. |
| `startWithSystem` | `true` | Start at login (also toggled from the tray menu). |
| `idleOpacity` | `0.6` | Widget opacity when nothing is running (0.2–1). |
| `width` | `340` | Widget width when expanded (260–700). Drag a side edge to change it. |
| `scale` | `1.0` | Zoom of the whole widget (0.75–1.75). `Ctrl` + mouse wheel, or tray → Size. |
| `collapsed` | `false` | Show only the header line. Toggled with the `▾`/`▴` button. |
| `left`, `bottom` | — | Saved position in pixels. Delete them to put the widget back in the bottom-right corner. |

## Files

| What | Windows | macOS | Linux |
|---|---|---|---|
| Log | `%LOCALAPPDATA%\RunnerWatch\logs\app.log` | `~/Library/Application Support/RunnerWatch/logs/app.log` | `~/.local/share/RunnerWatch/logs/app.log` |
| Saved token | Credential Manager (`RunnerWatch:github.com`) | Keychain (`RunnerWatch`) | Secret Service (`service=RunnerWatch`) |
