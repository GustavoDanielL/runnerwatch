using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using RunnerWatch.Core;

namespace RunnerWatch;

/// <summary>Tray / menu-bar icon: a dot with the state color, plus the app menu.</summary>
public sealed class Tray : IDisposable
{
    readonly TrayIcon _icon;
    readonly Dictionary<Color, WindowIcon> _icons = new();
    readonly NativeMenuItem _startup;

    public event Action? ToggleWindow, ToggleCollapsed, Refresh, OpenConfig, OpenLog, Reload, ChangeToken, Exit;
    public event Action<double>? SizeChosen;
    public event Action<bool>? StartupChanged;

    public Tray(Application app, bool startWithSystem)
    {
        var menu = new NativeMenu();
        menu.Items.Add(Item(OperatingSystem.IsWindows() ? L.T("tray.toggleHotkey") : L.T("tray.toggle"), () => ToggleWindow?.Invoke()));
        menu.Items.Add(Item(L.T("tray.collapse"), () => ToggleCollapsed?.Invoke()));
        var size = new NativeMenu();
        size.Items.Add(Item(L.T("size.small"), () => SizeChosen?.Invoke(0.85)));
        size.Items.Add(Item(L.T("size.normal"), () => SizeChosen?.Invoke(1.0)));
        size.Items.Add(Item(L.T("size.large"), () => SizeChosen?.Invoke(1.25)));
        menu.Items.Add(new NativeMenuItem(L.T("tray.size")) { Menu = size });
        menu.Items.Add(Item(L.T("tray.refresh"), () => Refresh?.Invoke()));
        menu.Items.Add(new NativeMenuItemSeparator());
        _startup = new NativeMenuItem(L.T("tray.startup")) { ToggleType = MenuItemToggleType.CheckBox, IsChecked = startWithSystem };
        _startup.Click += (_, _) =>
        {
            _startup.IsChecked = !_startup.IsChecked;
            StartupChanged?.Invoke(_startup.IsChecked);
        };
        menu.Items.Add(_startup);
        menu.Items.Add(Item(L.T("tray.config"), () => OpenConfig?.Invoke()));
        menu.Items.Add(Item(L.T("tray.log"), () => OpenLog?.Invoke()));
        menu.Items.Add(Item(L.T("tray.reload"), () => Reload?.Invoke()));
        menu.Items.Add(Item(L.T("tray.token"), () => ChangeToken?.Invoke()));
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(Item(L.T("tray.quit"), () => Exit?.Invoke()));

        _icon = new TrayIcon
        {
            Icon = Dot(Color.Parse("#6E7681")),
            ToolTipText = "RunnerWatch",
            Menu = menu,
            IsVisible = true,
        };
        _icon.Clicked += (_, _) => ToggleWindow?.Invoke();
        TrayIcon.SetIcons(app, [_icon]);
    }

    static NativeMenuItem Item(string header, Action onClick)
    {
        var item = new NativeMenuItem(header);
        item.Click += (_, _) => onClick();
        return item;
    }

    public void SetState(Color c, string tooltip)
    {
        _icon.Icon = Dot(c);
        _icon.ToolTipText = tooltip;
    }

    WindowIcon Dot(Color c)
    {
        if (_icons.TryGetValue(c, out var cached)) return cached;
        using var bmp = new RenderTargetBitmap(new PixelSize(32, 32));
        using (var ctx = bmp.CreateDrawingContext())
            ctx.DrawEllipse(new ImmutableSolidColorBrush(c), null, new Point(16, 16), 12, 12);
        using var ms = new MemoryStream();
        bmp.Save(ms, PngBitmapEncoderOptions.Default);
        ms.Position = 0;
        return _icons[c] = new WindowIcon(ms);
    }

    public void Dispose()
    {
        _icon.IsVisible = false;
        _icon.Dispose();
    }
}
