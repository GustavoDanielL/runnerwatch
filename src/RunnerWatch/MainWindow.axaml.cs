using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using RunnerWatch.Core;

namespace RunnerWatch;

public partial class MainWindow : Window
{
    readonly AppConfig _cfg = null!;
    readonly DispatcherTimer _tick = null!;
    readonly DispatcherTimer _saveDebounce = null!;
    bool _hover;
    int _bottom; // bottom edge in pixels: the window is anchored there and grows upwards
    bool _placed;

    public MainVm Vm { get; } = null!;
    public event Action? RefreshRequested;

    // Required by the XAML loader / previewer.
    public MainWindow() => InitializeComponent();

    public MainWindow(AppConfig cfg, MainVm vm)
    {
        _cfg = cfg;
        Vm = vm;
        InitializeComponent();
        DataContext = vm;
        Width = Math.Clamp(cfg.Width, 260, 600);

        _tick = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Vm.Tick());
        _saveDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _saveDebounce.Tick += (_, _) => { _saveDebounce.Stop(); SavePosition(); };

        HeaderBar.PointerPressed += Header_PointerPressed;
        RefreshBtn.PointerReleased += (_, e) => { e.Handled = true; RefreshRequested?.Invoke(); };
        Root.PointerEntered += (_, _) => { _hover = true; UpdateOpacity(); };
        Root.PointerExited += (_, _) => { _hover = false; UpdateOpacity(); };

        Opened += (_, _) => { HideFromAltTab(); PlaceInitially(); UpdateOpacity(); };
        PropertyChanged += (_, e) => { if (e.Property == BoundsProperty && _placed) Reposition(); };
        Closed += (_, _) => _tick.Stop();
    }

    public void ApplySnapshot(Snapshot s)
    {
        Vm.Apply(s);
        if (Vm.Runs.Count > 0) _tick.Start(); else _tick.Stop();
        UpdateOpacity();
    }

    // ---------- Position: anchored at the bottom, clamped to the monitor's work area ----------
    void PlaceInitially()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen == null) return;
        var wa = screen.WorkingArea;
        int margin = (int)(12 * screen.Scaling);
        int widthPx = (int)(Width * screen.Scaling);

        int left = _cfg.Left ?? wa.Right - widthPx - margin;
        int bottom = _cfg.Bottom ?? wa.Bottom - margin;
        // Saved monitor no longer connected: back to the default corner.
        if (Screens.ScreenFromPoint(new PixelPoint(left + widthPx / 2, bottom - 1)) == null)
        {
            left = wa.Right - widthPx - margin;
            bottom = wa.Bottom - margin;
        }
        Position = new PixelPoint(left, Position.Y);
        _bottom = bottom;
        _placed = true;
        Reposition();
    }

    void Reposition()
    {
        var screen = Screens.ScreenFromPoint(new PixelPoint(Position.X + 10, _bottom - 1)) ?? Screens.ScreenFromWindow(this);
        if (screen == null) return;
        var wa = screen.WorkingArea;
        int heightPx = (int)Math.Ceiling(Bounds.Height * screen.Scaling);
        int widthPx = (int)Math.Ceiling(Bounds.Width * screen.Scaling);
        int bottom = Math.Min(_bottom, wa.Bottom);
        int top = Math.Max(wa.Y, bottom - heightPx);
        int left = Math.Clamp(Position.X, wa.X, Math.Max(wa.X, wa.Right - widthPx));
        var p = new PixelPoint(left, top);
        if (p != Position) Position = p;
    }

    void Header_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || ReferenceEquals(e.Source, RefreshBtn)) return;
        BeginMoveDrag(e);
        // BeginMoveDrag returns once the OS drag loop ends on Windows; elsewhere the position settles shortly after.
        DispatcherTimer.RunOnce(() =>
        {
            var scaling = Screens.ScreenFromWindow(this)?.Scaling ?? 1;
            _bottom = Position.Y + (int)Math.Ceiling(Bounds.Height * scaling);
            Reposition();
            _bottom = Position.Y + (int)Math.Ceiling(Bounds.Height * scaling);
            _saveDebounce.Stop();
            _saveDebounce.Start();
        }, TimeSpan.FromMilliseconds(150));
    }

    void SavePosition()
    {
        _cfg.Left = Position.X;
        _cfg.Bottom = _bottom;
        _cfg.Save();
    }

    // ---------- Discreet: translucent when idle ----------
    void UpdateOpacity()
    {
        bool idle = !Vm.HasActiveRuns && Vm.FooterBrush != Palette.Stuck;
        Opacity = _hover || !idle ? 1.0 : Math.Clamp(_cfg.IdleOpacity, 0.2, 1.0);
    }

    // ---------- Click a run: open it ----------
    void Run_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton == MouseButton.Left && sender is Control { Tag: RunVm run })
            run.OpenCommand.Execute(null);
    }

    // ---------- Windows: keep out of Alt+Tab ----------
    const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000;
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hWnd, int index, int value);

    void HideFromAltTab()
    {
        if (!OperatingSystem.IsWindows() || TryGetPlatformHandle()?.Handle is not { } hwnd || hwnd == IntPtr.Zero) return;
        var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, (ex | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW);
    }

    // ---------- --record: PNG frames of the widget (README GIF) ----------
    public void SaveFrame(string path)
    {
        var scale = 2.0;
        var size = new PixelSize((int)Math.Ceiling(Root.Bounds.Width * scale), (int)Math.Ceiling(Root.Bounds.Height * scale));
        if (size.Width <= 0 || size.Height <= 0) return;
        using var bmp = new RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
        bmp.Render(Root);
        bmp.Save(path, PngBitmapEncoderOptions.Default);
    }
}
