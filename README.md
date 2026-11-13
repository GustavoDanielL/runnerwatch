# RunnerWatch

[![CI](https://github.com/GustavoDanielL/runnerwatch/actions/workflows/ci.yml/badge.svg)](https://github.com/GustavoDanielL/runnerwatch/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/GustavoDanielL/runnerwatch?sort=semver)](https://github.com/GustavoDanielL/runnerwatch/releases/latest)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue)](LICENSE)

A desktop widget that shows the GitHub Actions runs of your repositories: what is running, what is queued, and what is stuck. It also shows the CPU and RAM of self-hosted runners. Windows, macOS and Linux.

<img src="docs/media/demo.gif" width="360" alt="RunnerWatch in demo mode: a running deploy, a job queued behind a busy self-hosted runner, a stuck release and a failed run">

[Português](README.pt-BR.md)

## Install

Windows (PowerShell):

```powershell
irm https://raw.githubusercontent.com/GustavoDanielL/runnerwatch/main/install.ps1 | iex
```

macOS and Linux:

```bash
curl -fsSL https://raw.githubusercontent.com/GustavoDanielL/runnerwatch/main/install.sh | sh
```

Scoop:

```powershell
scoop install https://raw.githubusercontent.com/GustavoDanielL/runnerwatch/main/packaging/scoop/runnerwatch.json
```

The scripts install for the current user (no admin) and check the archive against `SHA256SUMS.txt` from the release. You can also download an archive from [Releases](https://github.com/GustavoDanielL/runnerwatch/releases/latest) yourself.

The binaries are not code-signed. On Windows, SmartScreen asks for confirmation the first time (More info → Run anyway). On macOS, install with the `curl` command above; a zip downloaded by a browser is blocked by Gatekeeper because the app is not notarized.

To try it without connecting to GitHub:

```bash
RunnerWatch --demo
```

## What it shows

- Running runs, with elapsed time and an estimate based on the median of the last 10 successful runs of the same workflow.
- Queued runs, jobs waiting for a runner, and runs waiting for an environment approval.
- Two kinds of waiting, kept apart:
  - **RUNNER BUSY**: the job waits because your self-hosted runner is working on another job. No notification.
  - **STUCK**: no runner has picked the job up for 5 minutes (configurable). Sends a notification.
- Deploys, detected by workflow name or by a deployment created for the commit. A notification is sent when one finishes.
- Failed runs, for 15 minutes after they fail. Right-click a failed run to re-run its failed jobs.
- Self-hosted runners with their status and CPU/RAM.

Click a run to open it on GitHub. On Windows, `Ctrl+Alt+G` shows and hides the widget; on macOS and Linux, use the tray icon. The language (English or Portuguese) and the light/dark theme follow the OS.

Size and placement:

- Drag the header to move the widget. Its bottom edge stays put, so it grows upwards as runs appear.
- Drag the left or right edge to change the width. `Ctrl` + mouse wheel, or tray → Size, scales everything.
- The `▾` button collapses it to a single line (status dot and counts) above the taskbar; `▴` expands it again. Notifications still arrive while it is collapsed.

All three are remembered between sessions.

## Authentication

RunnerWatch uses the first credential that works, in this order:

1. `GH_TOKEN` or `GITHUB_TOKEN`
2. a token saved from the app, kept in the OS keychain (Windows Credential Manager, macOS Keychain, Linux Secret Service)
3. `gh auth token` from the [GitHub CLI](https://cli.github.com/)
4. `git credential fill`, which covers Git Credential Manager on Windows

If none works, it asks for a token. SSH keys can't be used: the GitHub REST API doesn't accept them.

Token permissions: a classic token needs `repo`. A fine-grained token needs read access to Actions, Administration (to list runners), Deployments and Metadata, plus write access to Actions if you want to re-run jobs.

## Configuration

Settings live in `config.json` (tray icon → Open settings…). After editing, use Apply settings to restart. The common options:

```jsonc
{
  "repos": [],               // empty: your 10 most recently pushed repositories
  "stuckQueueMinutes": 5,
  "deployPattern": "deploy|release|publish|prod",
  "showFailuresMinutes": 15,
  "language": "auto",        // "en", "pt-BR"
  "theme": "system"          // "dark", "light"
}
```

For GitHub Enterprise Server, set `"apiBaseUrl": "https://HOST/api/v3/"`. Every option, the file locations per OS and the log path are in [docs/configuration.md](docs/configuration.md).

## Runner metrics

GitHub doesn't report CPU or RAM for runners, so RunnerWatch gets them another way:

- A self-hosted runner on the same machine as RunnerWatch (runner name equals machine name) is read locally. Nothing to install.
- A runner on another machine needs the [agent](agent/), a Python 3 script with no dependencies:

  ```bash
  sudo agent/install-linux.sh 9465 [token]                                       # Linux, systemd
  powershell -ExecutionPolicy Bypass -File agent\install-windows.ps1 -Port 9465   # Windows, as admin
  ```

  RunnerWatch looks for it at `http://<runner-name>:9465`. If the name doesn't resolve, map it in the config: `"agents": { "runner-name": "http://10.0.0.5:9465" }`. The agent serves aggregate CPU/RAM over plain HTTP; on a network you don't trust, use a token or a VPN.
- GitHub-hosted runners show their label only.

## How it works

RunnerWatch polls the REST API: every 10 s for a repository with active runs, every 60 s otherwise. Webhooks would need a public URL, which a desktop app doesn't have. Requests send `If-None-Match`, and `304` responses don't count against the rate limit.

It is written in C# on .NET 10 with Avalonia, and released as Native AOT binaries for x64 and arm64. [CONTRIBUTING.md](CONTRIBUTING.md) has a map of the code.

## Limitations

- The rate limit is 5,000 requests per hour per token. When it runs out, the footer shows when it resets.
- Only the latest 30 runs of each repository are read.
- Auto-discovery picks repositories by most recent push. A repository that only runs scheduled workflows has to be listed in `repos`.
- A deploy workflow is only detected if its name matches `deployPattern` or it creates a deployment.
- The global hotkey only exists on Windows. On GNOME, the tray icon requires the AppIndicator extension, which Ubuntu ships.
- The macOS build hasn't been tried on a Mac yet ([#2](https://github.com/GustavoDanielL/runnerwatch/issues/2)).

## Uninstall

```powershell
irm https://raw.githubusercontent.com/GustavoDanielL/runnerwatch/main/scripts/uninstall.ps1 | iex
```

```bash
curl -fsSL https://raw.githubusercontent.com/GustavoDanielL/runnerwatch/main/scripts/uninstall.sh | sh
```

To also delete the settings, the log and the saved token:

```powershell
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/GustavoDanielL/runnerwatch/main/scripts/uninstall.ps1))) -Purge
```

```bash
curl -fsSL https://raw.githubusercontent.com/GustavoDanielL/runnerwatch/main/scripts/uninstall.sh | sh -s -- --purge
```

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). `--demo` gives you data to work with, so no GitHub token is needed for UI changes. Issues labeled [good first issue](https://github.com/GustavoDanielL/runnerwatch/labels/good%20first%20issue) are a good place to start, and translations are a single JSON file.

## License

[MIT](LICENSE). RunnerWatch is not affiliated with GitHub.
