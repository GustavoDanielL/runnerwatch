# Contributing to RunnerWatch

Thanks for helping! Small PRs are great: a typo, a translation, a clearer error message all count.
Português? Pode abrir issue e PR em português, sem problema.

## Get it running (5 minutes)

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). That's it, on Windows, macOS or Linux.

```bash
git clone https://github.com/GustavoDanielL/runnerwatch
cd runnerwatch
dotnet test --project tests/RunnerWatch.Tests            # ~5 s
dotnet run --project src/RunnerWatch -- --demo           # fake data, no token, no network
```

Useful flags (`--help` lists them all):

| Flag | What it does |
|---|---|
| `--demo` | Looping scenario with fake data: running, queued, stuck, runner busy, deploy, failed |
| `--lang pt-BR` | Force a language |
| `--theme light` | Force a theme (`system`, `dark`, `light`) |
| `--record <dir>` | Demo + save PNG frames of the widget (how the README GIF is made) |

Without `--demo`, the app uses your real GitHub credentials (see the README) and only **reads** data, unless you click *Re-run*.

## Architecture

```
src/
  RunnerWatch.Core/          No UI. Cross-platform, AOT-compatible, fully unit-tested.
    ActionsMonitor.cs        The polling loop: repos → runs → jobs → snapshot. STUCK vs RUNNER BUSY lives here.
    DemoMonitor.cs           Fake data source (same ISnapshotSource interface as ActionsMonitor).
    GitHubApi.cs             Tiny REST client with ETag cache and rate-limit handling.
    Models.cs                GitHub DTOs, the Snapshot handed to the UI, and the JSON source generator context.
    TokenProvider.cs         Finds a token: env var → keychain → gh → Git Credential Manager.
    Platform/                Keychain access and a shell-free process runner.
    Metrics.cs, Startup.cs   Runner CPU/RAM and start-with-system, per OS.
    Strings.cs, i18n/*.json  Translations.
    Config.cs, Log.cs        Settings file and rotating, de-duplicated log.
  RunnerWatch/               Avalonia UI.
    App.axaml.cs             Wires everything: credentials, source, tray, notifications, UI error guard.
    MainWindow.axaml(.cs)    The widget: layout, position anchored to the bottom, drag, opacity.
    ViewModels.cs            CommunityToolkit.Mvvm view models (Snapshot → what you see).
    Tray.cs, Notifier.cs, GlobalHotkey.cs, TokenDialog.cs
tests/RunnerWatch.Tests/     xUnit v3. FakeGitHub.cs is an in-memory GitHub API for monitor tests.
agent/                       Optional Python agent that exposes CPU/RAM of a remote runner.
```

Data flows one way: `ISnapshotSource` (real or demo) → `Snapshot` → `MainVm.Apply` → XAML bindings. The polling loop runs on the thread pool; the UI only receives finished snapshots.

## Ground rules (the CI checks them)

- `dotnet build -warnaserror` passes on Windows, macOS and Linux.
- `dotnet test` passes. **Logic changes in `RunnerWatch.Core` need a test**; `FakeGitHub` makes that a few lines.
- `dotnet format --verify-no-changes` passes. Run `dotnet format` before committing.
- **Native AOT friendly**: no reflection-based JSON (add types to the `JsonSerializerContext` in `Models.cs`), compiled bindings in XAML (`x:DataType`).
- **No new runtime dependencies** without discussing it in an issue first: a small native binary is a feature.
- Bind `ProgressBar.Value` with `Mode=OneWay` (it's TwoWay by default and our VMs have no public setters; that bug once froze the app).
- Never log or display tokens.

## Translations

The easiest way to contribute, and no C# needed:

1. Copy `src/RunnerWatch.Core/i18n/en.json` to `i18n/<code>.json` (e.g. `es.json`, `fr.json`, `de-DE.json`).
2. Translate the values, keep the keys and the `{0}` placeholders.
3. Run `dotnet test`: it checks that every key and placeholder matches English.
4. Try it: `dotnet run --project src/RunnerWatch -- --demo --lang es`.

The language follows the OS automatically when the file exists.

## Pull requests

- One topic per PR; describe the *why*. Screenshots/GIFs for UI changes (use `--demo`).
- Conventional-ish titles help the changelog: `feat: …`, `fix: …`, `docs: …`, `i18n: …`.
- Not sure about an idea? Open an issue or a Discussion first, it saves everyone time.

## Releases (maintainers)

Update `CHANGELOG.md` and `<Version>` in `Directory.Build.props`, then push a tag `vX.Y.Z`. The [release workflow](.github/workflows/release.yml) builds native binaries for Windows, Linux and macOS (x64 and arm64), packages the macOS `.app`, and publishes them with SHA-256 checksums.

By contributing you agree that your contribution is licensed under the [MIT License](LICENSE) and you follow the [Code of Conduct](CODE_OF_CONDUCT.md).
