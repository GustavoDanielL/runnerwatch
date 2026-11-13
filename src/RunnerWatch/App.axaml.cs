using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using RunnerWatch.Core;
using RunnerWatch.Core.Platform;

namespace RunnerWatch;

public partial class App : Application, IRunActions
{
    public static Options Options { get; set; } = new(false, null, 45, null);

    Mutex? _single;
    AppConfig _cfg = null!;
    MainWindow _win = null!;
    Tray _tray = null!;
    Notifier _notifier = null!;
    GlobalHotkey? _hotkey;
    GitHubApi? _api;
    MetricsCollector? _metrics;
    ISnapshotSource? _source;
    CancellationTokenSource? _cts;
    IClassicDesktopStyleApplicationLifetime _desktop = null!;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
        _desktop = desktop;

        // Demo runs can coexist with the real app (handy for screenshots).
        _single = new Mutex(true, Options.Demo ? @"Local\RunnerWatch.Demo" : @"Local\RunnerWatch.SingleInstance", out var first);
        if (!first) { desktop.Shutdown(); return; }

        Log.Info($"Starting v{typeof(App).Assembly.GetName().Version?.ToString(3)} · {Environment.OSVersion} · .NET {Environment.Version}{(Options.Demo ? " · demo" : "")}");
        Dispatcher.UIThread.UnhandledException += OnUiException;
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Fatal("Unhandled exception (process will exit)", ex.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ex) => { Log.Error("Unobserved task exception", ex.Exception); ex.SetObserved(); };

        // The demo keeps its own settings so trying it never changes the real widget's size/position.
        _cfg = AppConfig.Load(Options.Demo ? Path.Combine(AppConfig.Dir, "config.demo.json") : null);
        L.Init(Options.Language ?? _cfg.Language);
        RequestedThemeVariant = (Options.Theme ?? _cfg.Theme).ToLowerInvariant() switch
        {
            "dark" => ThemeVariant.Dark,
            "light" => ThemeVariant.Light,
            _ => ThemeVariant.Default,
        };
        if (!Options.Demo) SystemStartup.Apply(_cfg.StartWithSystem);

        _win = new MainWindow(_cfg, new MainVm(this) { IsDemo = Options.Demo });
        _win.RefreshRequested += () => _source?.Kick();
        _win.Show();

        _notifier = new Notifier(() => _win);
        _hotkey = new GlobalHotkey(() => Dispatcher.UIThread.Post(ToggleWindow));

        _tray = new Tray(this, _cfg.StartWithSystem);
        _tray.ToggleWindow += ToggleWindow;
        _tray.ToggleCollapsed += () => { if (!_win.IsVisible) ToggleWindow(); _win.ToggleCollapsed(); };
        _tray.SizeChosen += s => _win.SetScale(s);
        _tray.Refresh += () => _source?.Kick();
        _tray.OpenConfig += () => OpenPath(_cfg.FilePath);
        _tray.OpenLog += () => OpenPath(File.Exists(Log.FilePath) ? Log.FilePath : Log.Dir);
        _tray.Reload += Restart;
        _tray.ChangeToken += () => _ = AskTokenAndRestartAsync(L.T("token.askNew"));
        _tray.Exit += Quit;
        _tray.StartupChanged += on => { _cfg.StartWithSystem = on; _cfg.Save(); SystemStartup.Apply(on); };

        if (Options.Demo) StartSource(new DemoMonitor());
        else _ = ConnectAsync();

        if (Options.RecordDir != null) StartRecording(Options.RecordDir, Options.RecordSeconds);
    }

    async Task ConnectAsync()
    {
        _cts = new CancellationTokenSource();
        TokenProvider.Auth? auth;
        var wait = TimeSpan.FromSeconds(5);
        while (true)
        {
            try
            {
                auth = await TokenProvider.ResolveAsync(_cfg.ApiBaseUrl, msg => Dispatcher.UIThread.Post(() => _win.Vm.Footer = msg), _cts.Token);
                break;
            }
            catch (TokenProvider.NetworkUnavailableException ex)
            {
                // Common right after boot: wait for the network instead of asking for a token.
                Log.Warn("No network while validating credentials; retrying", ex.InnerException);
                _win.Vm.Footer = L.F("status.waitingNetwork", wait.TotalSeconds.ToString("0"));
                try { await Task.Delay(wait, _cts.Token); } catch (OperationCanceledException) { return; }
                wait = TimeSpan.FromSeconds(Math.Min(60, wait.TotalSeconds * 2));
            }
            catch (OperationCanceledException) { return; }
        }

        if (auth == null)
        {
            Log.Warn("No GitHub credential found; asking for a token");
            await AskTokenAndRestartAsync(L.T("token.noCredential"));
            return;
        }

        _win.Vm.Footer = L.F("status.connected", auth.Login, auth.Source);
        Log.Info($"Connected as {auth.Login} via {auth.Source}");
        _api = new GitHubApi(auth.Token, _cfg.ApiBaseUrl);
        _metrics = new MetricsCollector(_cfg);
        var monitor = new ActionsMonitor(_cfg, _api, _metrics, auth.Login);
        monitor.AuthFailed += () => Dispatcher.UIThread.Post(() =>
        {
            Log.Warn($"Token rejected by GitHub (source: {auth.Source})");
            // The app's own token was revoked: delete it so we don't get stuck on it.
            if (auth.Source == L.T("source.saved")) CredentialStore.Delete();
            _ = AskTokenAndRestartAsync(L.T("token.rejected"));
        });
        StartSource(monitor);
    }

    void StartSource(ISnapshotSource source)
    {
        _cts ??= new CancellationTokenSource();
        _source = source;
        source.Updated += s => Dispatcher.UIThread.Post(() => OnSnapshot(s));
        source.Notify += (t, m, k) => Dispatcher.UIThread.Post(() => { if (_cfg.Notifications) _notifier.Show(t, m, k); });
        // Polling runs on the thread pool: it never competes with the UI thread.
        _ = Task.Run(() => source.RunAsync(_cts.Token));
    }

    void OnSnapshot(Snapshot s)
    {
        if (_uiFrozen) return;
        _win.ApplySnapshot(s);
        var vm = _win.Vm;
        var color = vm.HeaderBrush is ISolidColorBrush b ? b.Color : Colors.Gray;
        var tip = vm.RunningCount + vm.QueuedCount == 0
            ? L.T("tray.tipIdle")
            : L.F("tray.tipActive", vm.RunningCount, vm.QueuedCount) + (vm.AnyStuck ? " ⚠" : "");
        _tray.SetState(color, tip);
    }

    // ---------- IRunActions ----------
    public void Open(string url)
    {
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
        _ = _win.Launcher.LaunchUriAsync(new Uri(url));
    }

    public async Task RerunFailedAsync(RunView run)
    {
        if (_source == null) return;
        var error = await _source.RerunFailedAsync(run, _cts?.Token ?? default);
        _notifier.Show(error == null ? L.T("run.rerunDone") : L.T("badge.failed"),
            error ?? $"{run.Repo} · {run.Workflow}", error == null ? NotifyKind.Info : NotifyKind.Failure);
        _source.Kick();
    }

    // ---------- UI errors ----------
    // Swallowing every UI exception is dangerous: if it comes from layout, the framework re-runs layout, the
    // exception comes back, and the app spins forever (100% CPU, growing RAM, frozen window). Here: log it, and
    // if it turns into a storm, stop updating the UI and say so in the footer.
    readonly Queue<DateTime> _uiErrors = new();
    bool _uiFrozen;

    void OnUiException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        Log.Error("UI exception", e.Exception);

        var now = DateTime.UtcNow;
        _uiErrors.Enqueue(now);
        while (_uiErrors.Count > 0 && now - _uiErrors.Peek() > TimeSpan.FromSeconds(5)) _uiErrors.Dequeue();

        if (_uiErrors.Count >= 20 && !_uiFrozen)
        {
            _uiFrozen = true;
            Log.Fatal($"UI exception storm ({_uiErrors.Count} in 5 s). UI updates suspended.", e.Exception);
            try
            {
                _win.Vm.Runs.Clear();
                _win.Vm.Runners.Clear();
                _win.Vm.Footer = L.T("status.uiError");
                _win.Vm.FooterBrush = Palette.Stuck;
                _tray.SetState(Colors.Red, L.T("tray.tipUiError"));
            }
            catch (Exception inner) { Log.Error("Could not clear the UI", inner); }
        }
        else if (!_uiFrozen && _win != null)
        {
            _win.Vm.Footer = L.F("status.unexpected", e.Exception.Message);
        }
    }

    // ---------- Misc ----------
    void ToggleWindow()
    {
        if (_win.IsVisible) _win.Hide();
        else { _win.Show(); _win.Topmost = false; _win.Topmost = true; }
    }

    async Task AskTokenAndRestartAsync(string reason)
    {
        var token = await TokenDialog.AskAsync(reason, Open);
        if (token == null)
        {
            _win.Vm.Footer = L.T("status.noCredential");
            return;
        }
        try { CredentialStore.Write(token); }
        catch (Exception ex)
        {
            Log.Error("Could not save the token", ex);
            _win.Vm.Footer = L.F("error.generic", ex.Message);
            return;
        }
        Restart();
    }

    void OpenPath(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? path);
            var opener = OperatingSystem.IsWindows() ? null : OperatingSystem.IsMacOS() ? "open" : "xdg-open";
            if (opener == null) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            else Process.Start(new ProcessStartInfo(opener) { ArgumentList = { path }, UseShellExecute = false });
        }
        catch (Exception ex) { Log.Error($"Could not open {path}", ex); }
    }

    void StartRecording(string dir, int seconds)
    {
        Directory.CreateDirectory(dir);
        int frame = 0;
        var end = DateTime.UtcNow.AddSeconds(seconds);
        DispatcherTimer.Run(() =>
        {
            if (DateTime.UtcNow > end) { Quit(); return false; }
            try { _win.SaveFrame(Path.Combine(dir, $"frame_{frame++:00000}.png")); }
            catch (Exception ex) { Log.Error("Frame capture failed", ex); }
            return true;
        }, TimeSpan.FromMilliseconds(100));
    }

    void Restart()
    {
        // Release the mutex before starting the new instance.
        _single?.ReleaseMutex();
        _single?.Dispose();
        _single = null;
        if (Environment.ProcessPath is { } exe) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        Quit();
    }

    void Quit()
    {
        Log.Info("Exiting");
        Log.Flush();
        _cts?.Cancel();
        _hotkey?.Dispose();
        _tray?.Dispose();
        _api?.Dispose();
        _metrics?.Dispose();
        _desktop.Shutdown();
    }
}
