# Configuration

The settings file is created on first run:

| OS | Path |
|---|---|
| Windows | `%APPDATA%\RunnerWatch\config.json` |
| macOS | `~/Library/Application Support/RunnerWatch/config.json` |
| Linux | `~/.config/RunnerWatch/config.json` |

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
| `notifications` | `true` | Desktop notifications (stuck jobs, failures, finished deploys). |
| `onlyMine` | `false` | Only show runs triggered by you. |
| `agentUrlTemplate` | `http://{runner}:9465` | Where to find the metrics agent of a self-hosted runner. `""` disables. |
| `agents` | `{}` | Per-runner URL override: `{ "runner-name": "http://10.0.0.5:9465" }`. |
| `agentToken` | `null` | Sent as `X-Agent-Token` if the agents use `--token`. |
| `language` | `auto` | `auto` follows the OS, or a code such as `en` / `pt-BR`. |
| `theme` | `system` | `system`, `dark` or `light`. |
| `startWithSystem` | `true` | Start at login (also toggled from the tray menu). |
| `idleOpacity` | `0.6` | Widget opacity when nothing is running (0.2–1). |
| `width` | `340` | Widget width (260–600). |
| `left`, `bottom` | — | Saved position in pixels. Delete them to reset to the bottom-right corner. |

## Files

| What | Windows | macOS | Linux |
|---|---|---|---|
| Log | `%LOCALAPPDATA%\RunnerWatch\logs\app.log` | `~/Library/Application Support/RunnerWatch/logs/app.log` | `~/.local/share/RunnerWatch/logs/app.log` |
| Saved token | Credential Manager (`RunnerWatch:github.com`) | Keychain (`RunnerWatch`) | Secret Service (`service=RunnerWatch`) |
