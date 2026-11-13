using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace GhActionsMonitor;

public partial class App : Application
{
    Mutex? _single;
    AppConfig _cfg = null!;
    MainWindow _win = null!;
    Tray _tray = null!;
    GitHubApi? _api;
    MetricsCollector? _metrics;
    ActionsMonitor? _monitor;
    CancellationTokenSource? _cts;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _single = new Mutex(true, @"Local\GhActionsMonitor.SingleInstance", out var first);
        if (!first) { Shutdown(); return; }

        Log.Info($"Iniciando v{typeof(App).Assembly.GetName().Version} · {Environment.OSVersion} · .NET {Environment.Version}");
        DispatcherUnhandledException += OnUiException;
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
            Log.Fatal("Exceção não tratada (o processo vai encerrar)", ex.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            Log.Error("Exceção em Task não observada", ex.Exception);
            ex.SetObserved();
        };

        _cfg = AppConfig.Load();
        WindowsStartup.Apply(_cfg.StartWithWindows);

        _win = new MainWindow(_cfg);
        _win.RefreshRequested += () => _monitor?.Kick();
        _win.HotkeyPressed += ToggleWindow;
        _win.Show();

        _tray = new Tray(_cfg.StartWithWindows);
        _tray.ToggleWindow += ToggleWindow;
        _tray.Refresh += () => _monitor?.Kick();
        _tray.OpenConfig += () => Process.Start(new ProcessStartInfo(_cfg.FilePath) { UseShellExecute = true });
        _tray.Reload += Restart;
        _tray.ChangeToken += () => AskTokenAndRestart("Informe um novo token.");
        _tray.OpenLog += OpenLog;
        _tray.Exit += Quit;
        _tray.StartupChanged += on => { _cfg.StartWithWindows = on; _cfg.Save(); WindowsStartup.Apply(on); };

        await ConnectAsync();
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
                auth = await TokenProvider.ResolveAsync(_cfg.ApiBaseUrl, msg => Dispatcher.Invoke(() => _win.SetStatus(msg)), _cts.Token);
                break;
            }
            catch (TokenProvider.NetworkUnavailableException ex)
            {
                Log.Warn("Sem rede ao validar credencial; tentando de novo", ex.InnerException);
                // Comum logo após ligar o PC: espera a rede em vez de pedir token.
                _win.SetStatus($"Aguardando rede… (nova tentativa em {wait.TotalSeconds:0}s)");
                try { await Task.Delay(wait, _cts.Token); } catch (OperationCanceledException) { return; }
                wait = TimeSpan.FromSeconds(Math.Min(60, wait.TotalSeconds * 2));
            }
            catch (OperationCanceledException) { return; }
        }

        if (auth == null)
        {
            Log.Warn("Nenhuma credencial do GitHub encontrada; pedindo token");
            AskTokenAndRestart("Não encontrei credencial do GitHub nesta máquina (gh, Git Credential Manager ou GH_TOKEN). " +
                               "Chave SSH não serve para a API.");
            return;
        }

        _win.SetStatus($"Conectado como {auth.Login} ({auth.Source})");
        Log.Info($"Conectado como {auth.Login} via {auth.Source}");
        _api = new GitHubApi(auth.Token, _cfg.ApiBaseUrl);
        _metrics = new MetricsCollector(_cfg);
        _monitor = new ActionsMonitor(_cfg, _api, _metrics, auth.Login);
        _monitor.Updated += s => Dispatcher.BeginInvoke(() => OnSnapshot(s));
        _monitor.Notify += (t, m, k) => Dispatcher.BeginInvoke(() => _tray.Notify(t, m, k));
        _monitor.AuthFailed += () => Dispatcher.BeginInvoke(() =>
        {
            Log.Warn($"Token recusado pelo GitHub (fonte: {auth.Source})");
            // Token do próprio app foi revogado: apaga para não ficar preso nele.
            if (auth.Source == "token salvo no app") CredStore.Delete();
            AskTokenAndRestart("O GitHub recusou o token atual.");
        });

        // Loop em thread do pool: não disputa a thread da UI.
        _ = Task.Run(() => _monitor.RunAsync(_cts.Token));
    }

    // ---------- Erros de UI ----------
    // Engolir toda exceção de UI com Handled=true é perigoso: se ela vem do layout, o WPF refaz o
    // layout, a exceção volta, e o app entra em laço infinito (100% CPU, RAM crescendo, tela congelada).
    // Aqui: registra no log; se virar tempestade, para de atualizar a tela e avisa no rodapé.
    readonly System.Collections.Generic.Queue<DateTime> _uiErrors = new();
    bool _uiFrozen;

    void OnUiException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs ex)
    {
        ex.Handled = true;
        Log.Error("Exceção na interface", ex.Exception);

        var now = DateTime.UtcNow;
        _uiErrors.Enqueue(now);
        while (_uiErrors.Count > 0 && now - _uiErrors.Peek() > TimeSpan.FromSeconds(5)) _uiErrors.Dequeue();

        if (_uiErrors.Count >= 20 && !_uiFrozen)
        {
            _uiFrozen = true;
            Log.Fatal($"Tempestade de exceções na interface ({_uiErrors.Count} em 5s). Atualização da tela suspensa.", ex.Exception);
            try
            {
                _win.Vm.Runs.Clear();
                _win.Vm.Runners.Clear();
                _win.Vm.Footer = "Erro de interface. Bandeja → Abrir log, e reinicie.";
                _win.Vm.FooterBrush = Palette.Stuck;
                _tray?.SetState(Colors.Red, "Actions: erro de interface (veja o log)");
            }
            catch (Exception inner) { Log.Error("Falha ao limpar a interface", inner); }
        }
        else if (!_uiFrozen)
        {
            _win?.SetStatus("Erro inesperado: " + ex.Exception.Message);
        }
    }

    void OpenLog()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Log.Dir);
            var target = System.IO.File.Exists(Log.FilePath) ? Log.FilePath : Log.Dir;
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Error("Não abriu o log", ex); }
    }

    void OnSnapshot(Snapshot s)
    {
        if (_uiFrozen) return;
        _win.ApplySnapshot(s);
        var vm = _win.Vm;
        var color = ((SolidColorBrush)vm.HeaderBrush).Color;
        var tip = vm.RunningCount + vm.QueuedCount == 0
            ? "Actions: ocioso"
            : $"Actions: {vm.RunningCount} rodando, {vm.QueuedCount} na fila{(vm.AnyStuck ? " ⚠" : "")}";
        _tray.SetState(color, tip);
    }

    void ToggleWindow()
    {
        if (_win.IsVisible) _win.Hide();
        else { _win.Show(); _win.Topmost = false; _win.Topmost = true; }
    }

    void AskTokenAndRestart(string reason)
    {
        var token = TokenDialog.Ask(reason);
        if (token == null)
        {
            _win.SetStatus("Sem credencial. Use o menu da bandeja → Trocar token.");
            return;
        }
        CredStore.Write(token);
        Restart();
    }

    void Restart()
    {
        // Libera o mutex antes de abrir a nova instância.
        _single?.ReleaseMutex();
        _single?.Dispose();
        _single = null;
        if (Environment.ProcessPath is { } exe) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        Quit();
    }

    void Quit()
    {
        Log.Info("Encerrando");
        Log.Flush();
        _cts?.Cancel();
        _tray?.Dispose();
        _api?.Dispose();
        _metrics?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        base.OnExit(e);
    }
}
