# Changelog

All notable changes to this project are documented here. The format is based on [Keep a Changelog](https://keepachangelog.com/) and the project uses [Semantic Versioning](https://semver.org/).

## [0.2.0] - 2026-11-13

First public release, as **RunnerWatch** (previously a private Windows-only tool called GhActionsMonitor).

### Added
- Windows, macOS and Linux support (Avalonia 12, .NET 10, Native AOT binaries for x64 and arm64).
- **RUNNER BUSY** vs **STUCK**: a queue behind a busy self-hosted runner no longer raises an alarm.
- Failed runs stay visible for 15 minutes, with **Re-run failed jobs**.
- `--demo` mode with fake data, `--lang`, `--theme` and `--record`.
- English and Brazilian Portuguese, following the OS language; translations are plain JSON files.
- Light and dark themes following the system.
- Rotating, de-duplicated error log, and a guard that stops UI updates instead of spinning when the UI throws repeatedly.
- One-line installers for Windows, macOS and Linux that verify SHA-256 checksums; Scoop manifest.
- Test suite (xUnit v3) and CI on all three platforms.

### Fixed
- Freeze (100% CPU, growing memory) whenever a run was shown, caused by a TwoWay binding on a read-only property.
- Repositories from organizations and collaborations were ignored by auto-discovery.
- A run reported as "queued" while one of its jobs was already running was counted as queued.
- The widget could end up under the taskbar.

### Migration
- Settings and the saved token from GhActionsMonitor are migrated automatically on Windows; the old binary and autostart entry are removed by the installer.
