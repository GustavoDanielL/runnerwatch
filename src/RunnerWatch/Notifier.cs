using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using RunnerWatch.Core;
using RunnerWatch.Core.Platform;

namespace RunnerWatch;

/// <summary>
/// Desktop notifications. Linux: <c>notify-send</c>. macOS: <c>osascript</c> (title/text passed as argv, never
/// interpolated into the script). Windows, or when those are missing: a small toast window above the widget.
/// </summary>
public sealed class Notifier
{
    readonly Func<Window?> _anchor;
    Window? _toast;
    IDisposable? _timer;

    public Notifier(Func<Window?> anchor) => _anchor = anchor;

    public void Show(string title, string text, NotifyKind kind)
    {
        Log.Info($"Notification: {title} — {text}");
        if (OperatingSystem.IsLinux() && ProcessRunner.Exists("notify-send"))
        {
            var urgency = kind is NotifyKind.Failure or NotifyKind.Warning ? "critical" : "normal";
            _ = Task.Run(() => ProcessRunner.Run("notify-send", ["-a", "RunnerWatch", "-u", urgency, title, text]));
            return;
        }
        if (OperatingSystem.IsMacOS())
        {
            _ = Task.Run(() => ProcessRunner.Run("osascript",
                ["-e", "on run argv", "-e", "display notification (item 2 of argv) with title (item 1 of argv)", "-e", "end run", title, text]));
            return;
        }
        ShowToast(title, text, kind);
    }

    void ShowToast(string title, string text, NotifyKind kind)
    {
        _toast?.Close();
        _timer?.Dispose();

        var accent = kind switch
        {
            NotifyKind.Failure => Palette.Stuck,
            NotifyKind.Warning => Palette.Queued,
            NotifyKind.Success => Palette.Ok,
            _ => Palette.Running,
        };
        var body = new StackPanel { Spacing = 2 };
        body.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrWhiteSpace(text))
            body.Children.Add(new TextBlock { Text = text, FontSize = 11, Opacity = 0.75, TextWrapping = TextWrapping.Wrap });

        var row = new DockPanel();
        var bar = new Border { Width = 4, CornerRadius = new CornerRadius(2), Background = accent, Margin = new Thickness(0, 0, 10, 0) };
        DockPanel.SetDock(bar, Dock.Left);
        row.Children.Add(bar);
        row.Children.Add(body);

        var toast = new Window
        {
            WindowDecorations = WindowDecorations.None,
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent],
            Background = Brushes.Transparent,
            Topmost = true,
            ShowInTaskbar = false,
            ShowActivated = false,
            CanResize = false,
            SizeToContent = SizeToContent.Height,
            Width = 320,
            Content = new Border
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 10),
                Background = new ImmutableSolidColorBrush(Color.Parse("#F21F242C")),
                BorderBrush = new ImmutableSolidColorBrush(Color.Parse("#40FFFFFF")),
                BorderThickness = new Thickness(1),
                Child = row,
            },
        };
        TextElement.SetForeground(toast, new ImmutableSolidColorBrush(Color.Parse("#E6EDF3")));
        toast.PointerPressed += (_, _) => toast.Close();
        // Start off-screen and place once the real size is known (SizeToContent settles after the first layout).
        toast.WindowStartupLocation = WindowStartupLocation.Manual;
        toast.Position = new PixelPoint(-32000, -32000);
        toast.PropertyChanged += (_, e) => { if (e.Property == Visual.BoundsProperty && toast.Bounds.Height > 0) PlaceToast(toast); };
        toast.Show();
        _toast = toast;
        _timer = DispatcherTimer.RunOnce(() => toast.Close(), TimeSpan.FromSeconds(7));
    }

    void PlaceToast(Window toast)
    {
        var anchor = _anchor();
        var screen = (anchor != null ? toast.Screens.ScreenFromWindow(anchor) : null) ?? toast.Screens.Primary;
        if (screen == null) return;
        var s = screen.Scaling;
        int w = (int)(toast.Bounds.Width * s), h = (int)(toast.Bounds.Height * s), gap = (int)(10 * s);
        if (anchor is { IsVisible: true })
        {
            int x = Math.Clamp(anchor.Position.X + (int)(anchor.Bounds.Width * s) - w, screen.WorkingArea.X, screen.WorkingArea.Right - w);
            toast.Position = new PixelPoint(x, Math.Max(screen.WorkingArea.Y, anchor.Position.Y - h - gap));
        }
        else
        {
            toast.Position = new PixelPoint(screen.WorkingArea.Right - w - gap, screen.WorkingArea.Bottom - h - gap);
        }
    }
}
