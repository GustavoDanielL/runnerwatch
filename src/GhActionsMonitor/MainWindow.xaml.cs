using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace GhActionsMonitor;

public partial class MainWindow : Window
{
    const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000;
    const int WM_HOTKEY = 0x0312, HOTKEY_ID = 0x4741;
    const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_NOREPEAT = 0x4000, VK_G = 0x47;

    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hWnd, int index, int value);
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    readonly AppConfig _cfg;
    readonly DispatcherTimer _tick;
    readonly DispatcherTimer _saveDebounce;
    bool _hover;

    public MainVm Vm { get; } = new();
    public event Action? RefreshRequested;
    public event Action? HotkeyPressed;

    public MainWindow(AppConfig cfg)
    {
        _cfg = cfg;
        InitializeComponent();
        DataContext = Vm;
        Width = Math.Clamp(cfg.Width, 260, 600);

        _tick = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Vm.Tick(), Dispatcher);
        _saveDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _saveDebounce.Tick += (_, _) => { _saveDebounce.Stop(); SavePosition(); };

        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) => { PlaceInitially(); UpdateOpacity(); };
        SizeChanged += OnSizeChanged;
        Closed += (_, _) => _tick.Stop();
    }

    public void ApplySnapshot(Snapshot s)
    {
        Vm.Apply(s);
        if (Vm.Runs.Count > 0) _tick.Start(); else _tick.Stop();
        UpdateOpacity();
    }

    public void SetStatus(string text) => Vm.Footer = text;

    // ---------- Janela "ferramenta": fora do Alt+Tab e da barra de tarefas ----------
    void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, (ex | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW);

        // Ctrl+Alt+G mostra/esconde. Se outro app já usa o atalho, só não registra.
        RegisterHotKey(hwnd, HOTKEY_ID, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_G);
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        Closed += (_, _) => UnregisterHotKey(hwnd, HOTKEY_ID);
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
        {
            HotkeyPressed?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    // ---------- Posição: ancorada pelo fundo, cresce para cima ----------
    double _bottom;

    void PlaceInitially()
    {
        var wa = SystemParameters.WorkArea;
        var vLeft = SystemParameters.VirtualScreenLeft;
        var vTop = SystemParameters.VirtualScreenTop;
        var vRight = vLeft + SystemParameters.VirtualScreenWidth;
        var vBottom = vTop + SystemParameters.VirtualScreenHeight;

        double left = _cfg.Left ?? wa.Right - Width - 12;
        double bottom = _cfg.Bottom ?? wa.Bottom - 12;

        // Monitor onde estava foi desconectado: volta para o canto padrão.
        if (left < vLeft || left + Width > vRight || bottom > vBottom || bottom - 40 < vTop)
        {
            left = wa.Right - Width - 12;
            bottom = wa.Bottom - 12;
        }
        Left = left;
        _bottom = bottom;
        Top = _bottom - ActualHeight;
    }

    void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!IsLoaded) return;
        Top = Math.Max(SystemParameters.VirtualScreenTop, _bottom - ActualHeight);
    }

    void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed || ReferenceEquals(e.OriginalSource, RefreshBtn)) return;
        try { DragMove(); } catch (InvalidOperationException) { return; }
        _bottom = Top + ActualHeight;
        _saveDebounce.Stop();
        _saveDebounce.Start();
    }

    void SavePosition()
    {
        _cfg.Left = Math.Round(Left);
        _cfg.Bottom = Math.Round(_bottom);
        _cfg.Save();
    }

    // ---------- Discrição: semi-transparente quando ocioso ----------
    void Root_MouseEnter(object sender, MouseEventArgs e) { _hover = true; UpdateOpacity(); }
    void Root_MouseLeave(object sender, MouseEventArgs e) { _hover = false; UpdateOpacity(); }

    void UpdateOpacity()
    {
        bool idle = Vm.Runs.Count == 0 && Vm.FooterBrush != Palette.Stuck;
        Opacity = _hover || !idle ? 1.0 : Math.Clamp(_cfg.IdleOpacity, 0.2, 1.0);
    }

    // ---------- Ações ----------
    void Run_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string url } && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    void Refresh_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        RefreshRequested?.Invoke();
    }
}
