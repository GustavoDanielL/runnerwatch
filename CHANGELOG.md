# Changelog

All notable changes to this project are documented here. The format is based on [Keep a Changelog](https://keepachangelog.com/) and the project uses [Semantic Versioning](https://semver.org/).

## [0.3.3] - 2026-11-18

### Fixed
- The widget couldn't be moved to another monitor: if its content changed size during the drag (a run started or finished), it jumped back to where the drag began. Repositioning now waits for the drag to end.
- Moves the app didn't make itself (Win+Shift+arrow keys, window managers, the OS after a DPI change) were undone at the next update. They now count as the chosen position and are saved.

## [0.3.2] - 2026-11-17

### Fixed
- With a saved position, the widget could open far from it (e.g. in the middle of the screen) and stay there: the first layout pass reports a provisional size, and the "keep it on screen" clamp was applied to the current position. The widget now keeps the position you chose separately and always returns to it once the real size is known.
- The bottom edge could drift when the content grew: repositioning ran before the native window finished resizing.

## [0.3.1] - 2026-11-17

### Fixed
- The widget could freeze at a small size and cut its content after being dragged. On Windows, Avalonia treats any size change during the OS move loop as a user resize and stops sizing the window to its content; the widget now turns content sizing back on and logs it.

## [0.3.0] - 2026-11-13

### Added
- Resize the widget: drag the left or right edge (highlighted on hover) to change the width, and `Ctrl` + mouse wheel (or tray → Size) to scale it.
- Collapse the widget to a single line above the taskbar with the `▾` button, or from the tray menu.
- Width, scale and collapsed state are saved.
- The log records the screen size and scaling, to help with display-related bug reports.

### Fixed
- Without a saved position, the widget could open in the middle of the screen instead of the bottom-right corner.
- `--demo` wrote its window size and position into the real settings file; it now uses `config.demo.json`.

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
- A renamed or transferred repository made the app report "token rejected": redirects dropped the Authorization header, and any 401 was treated as a revoked token. Redirects are now followed on the same host only, and a 401 is confirmed with `/user` before asking for a new token.

### Migration
- Settings and the saved token from GhActionsMonitor are migrated automatically on Windows; the old binary and autostart entry are removed by the installer.
