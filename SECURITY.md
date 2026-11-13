# Security Policy

## Reporting a vulnerability

Please **don't open a public issue**. Use GitHub's private reporting instead:
**Security → Report a vulnerability** on this repository. You'll get an answer within a few days.

## What RunnerWatch does with your credentials

- It reads a GitHub token from `GH_TOKEN`/`GITHUB_TOKEN`, the OS keychain, `gh auth token` or Git Credential Manager, and sends it **only** to the configured GitHub API (`apiBaseUrl`).
- A token you paste in the app is stored in the OS keychain (Windows Credential Manager, macOS Keychain, Linux Secret Service), **never in a file**, and never written to the log.
- On macOS, saving a token calls `security add-generic-password -w <token>`: the token is visible in the process list for a fraction of a second. On a shared Mac, prefer `gh auth login`.
- It only **reads** data, except when you click **Re-run failed jobs**.
- External programs (`gh`, `git`, `security`, `secret-tool`, `notify-send`, `osascript`) are started without a shell, with arguments passed as a list: nothing is interpolated into a command line.

## The metrics agent

`agent/runner_agent.py` serves aggregate CPU/RAM over plain HTTP on port 9465. It doesn't execute anything it receives. On untrusted networks, use `--token`, bind to `127.0.0.1` behind a tunnel, or firewall the port.

## Supported versions

Only the latest release gets security fixes.
