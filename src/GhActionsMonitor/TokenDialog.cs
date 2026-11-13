using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace GhActionsMonitor;

/// <summary>Só aparece se nenhuma credencial da máquina funcionar (ex.: Git configurado só com SSH).</summary>
public static class TokenDialog
{
    const string NewTokenUrl =
        "https://github.com/settings/tokens/new?scopes=repo&description=GitHub%20Actions%20Monitor";

    public static string? Ask(string reason)
    {
        var win = new Window
        {
            Title = "GitHub Actions Monitor — token",
            Width = 460,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Topmost = true,
        };

        var link = new Hyperlink(new Run("Criar token clássico com escopo 'repo'")) { NavigateUri = new System.Uri(NewTokenUrl) };
        link.RequestNavigate += (_, e) => Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });

        var info = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        info.Inlines.Add(new Run(reason + "\n\n"));
        info.Inlines.Add(new Run("Opção mais fácil: instale o GitHub CLI e rode 'gh auth login', depois reabra o app.\n\nOu cole um token aqui. "));
        info.Inlines.Add(link);
        info.Inlines.Add(new Run(".\nToken fine-grained também serve: permissões de leitura em Actions, Administration (para listar runners), Deployments e Metadata.\n\nO token fica no Cofre de Credenciais do Windows, nunca em arquivo."));

        var box = new PasswordBox { Margin = new Thickness(0, 0, 0, 10), Padding = new Thickness(4) };
        string? result = null;

        var ok = new Button { Content = "Salvar", Width = 90, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancelar", Width = 90, IsCancel = true };
        ok.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(box.Password)) return;
            result = box.Password.Trim();
            win.DialogResult = true;
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(info);
        root.Children.Add(box);
        root.Children.Add(buttons);
        win.Content = root;
        win.Loaded += (_, _) => box.Focus();

        return win.ShowDialog() == true ? result : null;
    }
}
