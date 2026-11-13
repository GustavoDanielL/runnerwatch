using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using WinForms = System.Windows.Forms;

namespace GhActionsMonitor;

/// <summary>Ícone na bandeja: bolinha com a cor do estado + menu + notificações.</summary>
public sealed class Tray : IDisposable
{
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);

    readonly WinForms.NotifyIcon _icon;
    readonly Dictionary<Color, Icon> _icons = new();
    readonly WinForms.ToolStripMenuItem _startup;

    public event Action? ToggleWindow, Refresh, OpenConfig, Reload, ChangeToken, Exit;
    public event Action<bool>? StartupChanged;

    public Tray(bool startWithWindows)
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Mostrar / esconder (Ctrl+Alt+G)", null, (_, _) => ToggleWindow?.Invoke());
        menu.Items.Add("Atualizar agora", null, (_, _) => Refresh?.Invoke());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        _startup = new WinForms.ToolStripMenuItem("Iniciar com o Windows") { Checked = startWithWindows, CheckOnClick = true };
        _startup.CheckedChanged += (_, _) => StartupChanged?.Invoke(_startup.Checked);
        menu.Items.Add(_startup);
        menu.Items.Add("Abrir configuração…", null, (_, _) => OpenConfig?.Invoke());
        menu.Items.Add("Aplicar configuração (reiniciar)", null, (_, _) => Reload?.Invoke());
        menu.Items.Add("Trocar token do GitHub…", null, (_, _) => ChangeToken?.Invoke());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Sair", null, (_, _) => Exit?.Invoke());

        _icon = new WinForms.NotifyIcon
        {
            Icon = Dot(Color.FromArgb(0x6E, 0x76, 0x81)),
            Text = "GitHub Actions Monitor",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == WinForms.MouseButtons.Left) ToggleWindow?.Invoke(); };
    }

    public void SetState(System.Windows.Media.Color c, string tooltip)
    {
        _icon.Icon = Dot(Color.FromArgb(c.R, c.G, c.B));
        _icon.Text = tooltip.Length > 63 ? tooltip[..63] : tooltip; // limite do Windows
    }

    public void Notify(string title, string text, NotifyKind kind)
    {
        var tip = kind switch
        {
            NotifyKind.Failure => WinForms.ToolTipIcon.Error,
            NotifyKind.Warning => WinForms.ToolTipIcon.Warning,
            _ => WinForms.ToolTipIcon.Info,
        };
        _icon.ShowBalloonTip(5000, title, string.IsNullOrWhiteSpace(text) ? " " : text, tip);
    }

    Icon Dot(Color c)
    {
        if (_icons.TryGetValue(c, out var cached)) return cached;
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var b = new SolidBrush(c);
            g.FillEllipse(b, 4, 4, 24, 24);
        }
        var h = bmp.GetHicon();
        try { return _icons[c] = (Icon)Icon.FromHandle(h).Clone(); }
        finally { DestroyIcon(h); }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        foreach (var i in _icons.Values) i.Dispose();
    }
}
